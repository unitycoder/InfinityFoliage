using System;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine.Rendering;
using InfinityTech.Core.Geometry;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Landscape.FoliagePipeline
{
    internal static class TreeShaderID
    {
        internal static int offset = Shader.PropertyToID("_TreeIndexOffset");
        internal static int indexBuffer = Shader.PropertyToID("_TreeIndexBuffer");
        internal static int elementBuffer = Shader.PropertyToID("_TreeElementBuffer");
    }

    [Serializable]
    public unsafe class FTreeSubSector
    {
        private NativeList<FTreeSection> m_TreeSections;
    }

    [Serializable]
    public unsafe class FTreeSector
    {
        public FMesh tree;
        public int treeIndex;
        public int cullDistance = 256;
        public List<FTransform> transforms;

        private ComputeBuffer m_TreeIndexBuffer;
        private ComputeBuffer m_TreeElementBuffer;

        private NativeArray<float> m_TreeLODInfos;
        private NativeArray<FTreeElement> m_TreeElements;
        private NativeList<FTreeSection> m_TreeSections;
        private NativeList<int> m_PassTreeSections;
        private NativeArray<int> m_ViewTreeElements;
        private NativeArray<int> m_PassTreeElements;
        private NativeList<FTreeDrawCommand> m_TreeDrawCommands;

        public void Initialize()
        {
            m_TreeElements = new NativeArray<FTreeElement>(transforms.Count, Allocator.Persistent);
            m_TreeSections = new NativeList<FTreeSection>(4096, Allocator.Persistent);
        }

        public void Release()
        {
            m_TreeLODInfos.Dispose();
            m_TreeElements.Dispose();
            m_TreeSections.Dispose();
            m_ViewTreeElements.Dispose();
            m_PassTreeElements.Dispose();
            m_PassTreeSections.Dispose();
            m_TreeDrawCommands.Dispose();

            m_TreeIndexBuffer.Dispose();
            m_TreeElementBuffer.Dispose();
        }

        public void BuildTreeElement()
        {
            for (int i = 0; i < transforms.Count; ++i)
            {
                float4x4 matrixWorld = float4x4.TRS(transforms[i].position, quaternion.EulerXYZ(transforms[i].rotation), transforms[i].scale);

                FTreeElement treeElement;
                treeElement.meshIndex = 0;
                treeElement.matrix_World = matrixWorld;
                treeElement.boundBox = Geometry.CaculateWorldBound(tree.boundBox, matrixWorld);
                treeElement.boundSphere = new FSphere(Geometry.CaculateBoundRadius(treeElement.boundBox), treeElement.boundBox.center);
                m_TreeElements[i] = treeElement;
            }
        }

        public void BuildTreeSection()
        {
            for (int i = 0; i < transforms.Count; ++i)
            {
                FTreeSection treeSection;
                treeSection.batchIndex = i;

                for (int j = 0; j < tree.numLOD; ++j)
                {
                    treeSection.meshIndex = j;

                    for (int k = 0; k < tree.numSections[j]; ++k)
                    {
                        treeSection.sectionIndex = k;
                        treeSection.materialIndex = tree.lODInfo[j].materialSlot[k];
                        m_TreeSections.Add(treeSection);
                    }
                }
            }

            m_TreeSections.Sort();
        }

        public void BuildTreeBuffer()
        {
            m_TreeLODInfos = new NativeArray<float>(tree.lODInfo.Length, Allocator.Persistent);
            for (var j = 0; j < tree.lODInfo.Length; ++j)
            {
                m_TreeLODInfos[j] = tree.lODInfo[j].screenSize;
            }

            m_TreeDrawCommands = new NativeList<FTreeDrawCommand>(6, Allocator.Persistent);
            m_ViewTreeElements = new NativeArray<int>(m_TreeElements.Length, Allocator.Persistent);
            m_PassTreeSections = new NativeList<int>(m_TreeElements.Length, Allocator.Persistent);
            m_PassTreeElements = new NativeArray<int>(m_TreeSections.Length, Allocator.Persistent);

            m_TreeIndexBuffer = new ComputeBuffer(m_PassTreeElements.Length, Marshal.SizeOf(typeof(int)));
            m_TreeElementBuffer = new ComputeBuffer(m_TreeElements.Length, Marshal.SizeOf(typeof(FTreeElement)));
            m_TreeElementBuffer.SetData(m_TreeElements);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JobHandle InitView(in float cullDistance, in float3 viewPos, in float4x4 matrixProj, FPlane* planes)
        {
            var treeViewProcessJob = new FTreeBatchCullingJob();
            {
                treeViewProcessJob.planes = planes;
                treeViewProcessJob.numLOD = m_TreeLODInfos.Length - 1;
                treeViewProcessJob.viewOringin = viewPos;
                treeViewProcessJob.matrix_Proj = matrixProj;
                treeViewProcessJob.maxDistance = cullDistance;
                treeViewProcessJob.treeLODInfos = (float*)m_TreeLODInfos.GetUnsafePtr();
                treeViewProcessJob.treeElements = (FTreeElement*)m_TreeElements.GetUnsafePtr();
                treeViewProcessJob.viewTreeElements = m_ViewTreeElements;
            }
            return treeViewProcessJob.Schedule(m_ViewTreeElements.Length, 256);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JobHandle DispatchSetup()
        {
            var treeDrawCommandBuildJob = new FTreeDrawCommandBuildJob();
            {
                treeDrawCommandBuildJob.maxLOD = tree.lODInfo.Length - 1;
                treeDrawCommandBuildJob.treeSections = m_TreeSections;
                treeDrawCommandBuildJob.treeElements = (FTreeElement*)m_TreeElements.GetUnsafePtr();
                treeDrawCommandBuildJob.viewTreeElements = m_ViewTreeElements;
                treeDrawCommandBuildJob.passTreeElements = m_PassTreeElements;
                treeDrawCommandBuildJob.passTreeSections = m_PassTreeSections;
                treeDrawCommandBuildJob.treeDrawCommands = m_TreeDrawCommands;
            }
            return treeDrawCommandBuildJob.Schedule();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DispatchDraw(CommandBuffer cmdBuffer, in int passIndex, MaterialPropertyBlock propertyBlock)
        {
            m_TreeIndexBuffer.SetData(m_PassTreeElements);
            //cmdBuffer.SetComputeBufferData(m_IndexBuffer, m_TreeBatchIndexs);

            foreach (var treeDrawCmd in m_TreeDrawCommands)
            {
                Mesh mesh = tree.meshes[treeDrawCmd.meshIndex];
                Material material = tree.materials[treeDrawCmd.materialIndex];

                propertyBlock.Clear();
                propertyBlock.SetInt(TreeShaderID.offset, treeDrawCmd.countOffset.y);
                propertyBlock.SetBuffer(TreeShaderID.indexBuffer, m_TreeIndexBuffer);
                propertyBlock.SetBuffer(TreeShaderID.elementBuffer, m_TreeElementBuffer);
                cmdBuffer.DrawMeshInstancedProcedural(mesh, treeDrawCmd.sectionIndex, material, passIndex, treeDrawCmd.countOffset.x, propertyBlock);
            }

            m_PassTreeSections.Clear();
            m_TreeDrawCommands.Clear();
        }

#if UNITY_EDITOR
        public void DrawBounds(in bool lodColorState = false, in bool showSphere = false)
        {
            if (Application.isPlaying == false) { return; }

            for (var i = 0; i < m_TreeElements.Length; ++i)
            {
                var treeBatch = m_TreeElements[i];
                ref var color = ref Geometry.LODColors[treeBatch.meshIndex];
                if (m_ViewTreeElements[i] == 0)
                {
                    continue;
                }

                Geometry.DrawBound(treeBatch.boundBox, lodColorState ? color : Color.blue);

                if (showSphere)
                {
                    UnityEditor.Handles.color = lodColorState ? color : Color.yellow;
                    UnityEditor.Handles.DrawWireDisc(treeBatch.boundSphere.center, Vector3.up, treeBatch.boundSphere.radius);
                    UnityEditor.Handles.DrawWireDisc(treeBatch.boundSphere.center, Vector3.back, treeBatch.boundSphere.radius);
                    UnityEditor.Handles.DrawWireDisc(treeBatch.boundSphere.center, Vector3.right, treeBatch.boundSphere.radius);
                }
            }
        }
#endif
    }
}