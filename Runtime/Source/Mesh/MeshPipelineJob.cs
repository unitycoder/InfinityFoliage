using Unity.Jobs;
using Unity.Burst;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using InfinityTech.Core.Geometry;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using static Unity.Mathematics.mathExtent;

namespace Landscape.FoliagePipeline
{
#if UNITY_EDITOR
    interface ITask
    {
        void Execute();
    }

    public struct FUpdateTreeTask : ITask
    {
        public int length;
        public float2 size;
        public float3 terrainPosition;
        public TreePrototype treePrototype;
        public TreeInstance[] treeInstances;
        public TreePrototype[] treePrototypes;
        public List<FTransform> treeTransfroms;


        public void Execute()
        {
            FTransform transform = new FTransform();

            for (int i = 0; i < length; ++i)
            {
                ref TreeInstance treeInstance = ref treeInstances[i];
                TreePrototype serchTreePrototype = treePrototypes[treeInstance.prototypeIndex];
                if (serchTreePrototype.Equals(treePrototype))
                {
                    transform.rotation = new float3(0, treeInstance.rotation, 0);
                    transform.position = (treeInstance.position * new float3(size.x, size.y, size.x)) + terrainPosition;
                    transform.scale = new float3(treeInstance.widthScale, treeInstance.heightScale, treeInstance.widthScale);
                    treeTransfroms.Add(transform);
                }
            }
        }
    }

    public struct FUpdateGrassTask : ITask
    {
        public int length;
        public int[] dscDensity;
        public int[,] srcDensity;
        public float[,] srcHeight;
        public float[] dscHeight;
        public FGrassSection grassSection;


        public void Execute()
        {
            for (int j = 0; j < length; ++j)
            {
                for (int k = 0; k < length; ++k)
                {
                    int densityIndex = j * length + k;
                    dscHeight[densityIndex] = srcHeight[j, k];
                    dscDensity[densityIndex] = srcDensity[j, k];
                    grassSection.totalDensity += srcDensity[j, k];
                }
            }
        }
    }

    public struct FUpdateFoliageJob : IJob
    {
        public GCHandle taskHandle;

        public void Execute()
        {
            ITask task = (ITask)taskHandle.Target;
            task.Execute();
        }
    }
#endif

    [BurstCompile]
    public unsafe struct FTreeBatchLODJob : IJobParallelFor
    {
        [ReadOnly]
        public float3 viewOringin;

        [ReadOnly]
        public float4x4 matrix_Proj;

        [ReadOnly]
        public NativeArray<float> treeBatchLODs;

        [NativeDisableUnsafePtrRestriction]
        public FTreeElement* treeBatchs;


        public void Execute(int index)
        {
            float screenRadiusSquared = 0;
            ref FTreeElement treeBatch = ref treeBatchs[index];

            for (int i = treeBatchLODs.Length - 1; i >= 0; --i)
            {
                float LODSize = (treeBatchLODs[i] * treeBatchLODs[i]) * 0.5f;
                if (screenRadiusSquared < LODSize)
                {
                    treeBatch.meshIndex = i;
                    break;
                }
            }
        }
    }

    [BurstCompile]
    public unsafe struct FGrassScatterJob : IJob
    {
        [ReadOnly]
        public int split;

        //[ReadOnly]
        //public float heightScale;

        [ReadOnly]
        public float uniqueValue;

        [ReadOnly]
        public float densityScale;

        [ReadOnly]
        public float3 sectionPivot;

        [ReadOnly]
        public float4 widthScale;

        [ReadOnly]
        public NativeArray<int> densityMap;

        //[ReadOnly]
        //public NativeArray<float> heightMap;

        [WriteOnly]
        public NativeList<FGrassBatch> grassBatchs;


        public void Execute()
        {
            int density;
            //float height;
            float3 scale;
            float3 position;
            float3 newPosition;
            float4x4 matrix_World;
            FGrassBatch grassBatch = default;

            for (int i = 0; i < densityMap.Length; ++i)
            {
                //height = heightMap[i];
                density = (int)((float)densityMap[i] * densityScale);
                position = sectionPivot + new float3(i % split, 0 /*height * heightScale*/, i / split);

                for (int j = 0; j < density; ++j)
                {
                    float multiplier = (j + 1) * math.abs(uniqueValue);
                    float randomRotate = randomFloat(((position.x + 0.5f) * multiplier) + (position.z + 0.5f));
                    float2 randomPoint = randomFloat2(new float2(position.x + 0.5f, (position.z + 0.5f) * multiplier));
                    newPosition = position + new float3(randomPoint.x, 0, randomPoint.y);

                    float randomScale = randomFloat((newPosition.x + newPosition.z) * multiplier);
                    float yScale = widthScale.z + ((widthScale.w - widthScale.z) * randomScale);
                    float xzScale = widthScale.x + ((widthScale.y - widthScale.x) * randomScale);
                    scale = new float3(xzScale, yScale, xzScale);
                    matrix_World = float4x4.TRS(newPosition, quaternion.AxisAngle(new float3(0, 1, 0), math.radians(randomRotate * 360)), scale);

                    grassBatch.position = newPosition;
                    grassBatch.matrix_World = matrix_World;
                    grassBatchs.Add(grassBatch);
                }
            }
        }
    }

    [BurstCompile]
    public unsafe struct FBoundCullingJob : IJob
    {
        public int length;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FPlane* planes;

        [NativeDisableUnsafePtrRestriction]
        public FBound* sectorBounds;

        [WriteOnly]
        public NativeArray<int> visibleMap;


        public void Execute()
        {
            for (int index = 0; index < length; ++index)
            {
                int visible = 1;
                float2 distRadius = new float2(0, 0);
                ref FBound sectorBound = ref sectorBounds[index];

                for (int PlaneIndex = 0; PlaneIndex < 6; ++PlaneIndex)
                {
                    ref FPlane plane = ref planes[PlaneIndex];
                    distRadius.x = math.dot(plane.normalDist.xyz, sectorBound.center) + plane.normalDist.w;
                    distRadius.y = math.dot(math.abs(plane.normalDist.xyz), sectorBound.extents);

                    visible = math.select(visible, 0, distRadius.x + distRadius.y < 0);
                }
                visibleMap[index] = visible;
            }
        }
    }

    [BurstCompile]
    public unsafe struct FBoundCullingParallelJob : IJobParallelFor
    {
        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FPlane* planes;

        [NativeDisableUnsafePtrRestriction]
        public FBound* sectorBounds;

        [WriteOnly]
        public NativeArray<int> visibleMap;


        public void Execute(int index)
        {
            int visible = 1;
            float2 distRadius = new float2(0, 0);
            ref FBound sectorBound = ref sectorBounds[index];

            for (int PlaneIndex = 0; PlaneIndex < 6; ++PlaneIndex)
            {
                ref FPlane plane = ref planes[PlaneIndex];
                distRadius.x = math.dot(plane.normalDist.xyz, sectorBound.center) + plane.normalDist.w;
                distRadius.y = math.dot(math.abs(plane.normalDist.xyz), sectorBound.extents);

                visible = math.select(visible, 0, distRadius.x + distRadius.y < 0);
            }
            visibleMap[index] = visible;
        }
    }

    [BurstCompile]
    public unsafe struct FGrassSectionCullingJob : IJobParallelFor
    {
        [ReadOnly]
        public float maxDistance;

        [ReadOnly]
        public float3 viewOrigin;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FPlane* planes;

        [NativeDisableUnsafePtrRestriction]
        public FBoundSection* sectionBounds;

        [WriteOnly]
        public NativeArray<int> visibleMap;


        public void Execute(int index)
        {
            int visible = 1;
            float2 distRadius = new float2(0, 0);
            ref FBoundSection sectionBound = ref sectionBounds[index];

            for (int PlaneIndex = 0; PlaneIndex < 6; ++PlaneIndex)
            {
                ref FPlane plane = ref planes[PlaneIndex];
                distRadius.x = math.dot(plane.normalDist.xyz, sectionBound.boundBox.center) + plane.normalDist.w;
                distRadius.y = math.dot(math.abs(plane.normalDist.xyz), sectionBound.boundBox.extents);

                visible = math.select(visible, 0, distRadius.x + distRadius.y < 0);
            }
            visibleMap[index] = math.select(visible, 0, math.distance(viewOrigin, sectionBound.boundBox.center) > maxDistance);
        }
    }

    [BurstCompile]
    public unsafe struct FTreeBatchCullingJob : IJobParallelFor
    {
        [ReadOnly]
        public int numLOD;

        [ReadOnly]
        public float maxDistance;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FPlane* planes;

        [ReadOnly]
        public float3 viewOringin;

        [ReadOnly]
        public float4x4 matrix_Proj;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public float* treeLODInfos;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FTreeElement* treeElements;

        [WriteOnly]
        public NativeArray<int> viewTreeElements;


        public void Execute(int index)
        {
            ref FTreeElement treeElement = ref treeElements[index];

            //Calculate LOD
            float ScreenRadiusSquared = Geometry.ComputeBoundsScreenRadiusSquared(treeElement.boundSphere.radius, treeElement.boundBox.center, viewOringin, matrix_Proj);

            for (int LODIndex = numLOD; LODIndex >= 0; --LODIndex)
            {
                ref float TreeLODInfo = ref treeLODInfos[LODIndex];

                if (mathExtent.sqr(TreeLODInfo * 0.5f) >= ScreenRadiusSquared)
                {
                    treeElement.meshIndex = LODIndex;
                    break;
                }
            }

            //Culling Batch
            int visible = 1;
            float2 distRadius = new float2(0, 0);

            for (int PlaneIndex = 0; PlaneIndex < 6; ++PlaneIndex)
            {
                ref FPlane plane = ref planes[PlaneIndex];
                distRadius.x = math.dot(plane.normalDist.xyz, treeElement.boundBox.center) + plane.normalDist.w;
                distRadius.y = math.dot(math.abs(plane.normalDist.xyz), treeElement.boundBox.extents);

                visible = math.select(visible, 0, distRadius.x + distRadius.y < 0);
            }
            viewTreeElements[index] = math.select(visible, 0, math.distance(viewOringin, treeElement.boundBox.center) > maxDistance);
        }
    }

    [BurstCompile]
    public unsafe struct FTreeDrawCommandBuildJob : IJob
    {
        public int maxLOD;

        [ReadOnly]
        [NativeDisableUnsafePtrRestriction]
        public FTreeElement* treeElements;

        [ReadOnly]
        public NativeArray<int> viewTreeElements;

        [ReadOnly]
        public NativeList<FTreeSection> treeSections;

        [WriteOnly]
        public NativeArray<int> passTreeElements;

        public NativeList<FTreeSection> passTreeSections;

        public NativeList<FMeshDrawCommand> treeDrawCommands;


        public void Execute()
        {
            //Gather PassTreeElement
            FTreeSection treeElement;
            for (int i = 0; i < treeSections.Length; ++i)
            {
                treeElement = treeSections[i];
                ref FTreeElement treeSection = ref treeElements[treeElement.batchIndex];

                if (viewTreeElements[treeElement.batchIndex] != 0 && treeElement.meshIndex == treeSection.meshIndex)
                {
                    passTreeSections.Add(treeElement);
                }
            }

            //Build TreeDrawCommand
            FTreeSection passTreeSection;
            FTreeSection cachePassTreeSection = new FTreeSection(-1, -1, -1, -1);

            FMeshDrawCommand treeDrawCommand;
            FMeshDrawCommand cacheTreeDrawCommand;

            for (int i = 0; i < passTreeSections.Length; ++i)
            {
                passTreeSection = passTreeSections[i];
                passTreeElements[i] = passTreeSection.batchIndex;

                if (!passTreeSection.Equals(cachePassTreeSection))
                {
                    cachePassTreeSection = passTreeSection;

                    treeDrawCommand.countOffset.x = 0;
                    treeDrawCommand.countOffset.y = i;
                    treeDrawCommand.meshIndex = passTreeSection.meshIndex;
                    treeDrawCommand.sectionIndex = passTreeSection.sectionIndex;
                    treeDrawCommand.materialIndex = passTreeSection.materialIndex;
                    //TreeDrawCommand.InstanceGroupID = PassTreeSection.InstanceGroupID;
                    treeDrawCommands.Add(treeDrawCommand);
                }

                cacheTreeDrawCommand = treeDrawCommands[treeDrawCommands.Length - 1];
                cacheTreeDrawCommand.countOffset.x += 1;
                treeDrawCommands[treeDrawCommands.Length - 1] = cacheTreeDrawCommand;
            }
        }
    }
}
