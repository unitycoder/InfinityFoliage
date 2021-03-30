using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine.Rendering;
using InfinityTech.Core.Geometry;
using System.Runtime.CompilerServices;

namespace Landscape.FoliagePipeline
{
    [RequireComponent(typeof(BoundComponent))]
    [AddComponentMenu("HG/Foliage/Grass Component")]
    public unsafe class GrassComponent : FoliageComponent
    {
#if UNITY_EDITOR
        [Header("Debug")]
        public bool showBounds = false;
#endif

        private float lastDensityScale;
        public bool needUpdateGPU
        {
            get
            {
                return lastDensityScale != terrain.detailObjectDensity;
            }
        }

        [HideInInspector]
        public Terrain terrain;
        [HideInInspector]
        public TerrainData terrainData;
        [HideInInspector]
        public FGrassSector[] grassSectors;
        [HideInInspector]
        public BoundComponent boundComponent;


        protected override void OnRegister()
        {
            terrain = GetComponent<Terrain>();
            terrainData = terrain.terrainData;
            boundComponent = GetComponent<BoundComponent>();
            boundComponent.grassComponent = this;

            InitGrassSectors();
            lastDensityScale = -1;

            if (terrain.drawTreesAndFoliage == true)
            {
                terrain.drawTreesAndFoliage = false;
            }
        }

        protected override void OnTransformChange()
        {

        }

        protected override void EventPlay()
        {

        }

        protected override void EventTick()
        {

        }

        protected override void UnRegister()
        {
            ReleaseGrassSectors();
            boundComponent.grassComponent = null;
        }

#if UNITY_EDITOR
        private void DrawBounds()
        {
            if (showBounds == false || Application.isPlaying == false || this.enabled == false || this.gameObject.activeSelf == false) return;

            foreach (FGrassSector grassSector in grassSectors)
            {
                grassSector.DrawBounds();
            }
        }

        protected virtual void OnDrawGizmosSelected()
        {
            DrawBounds();
        }
#endif

        #region Grass
        private void InitGrassSectors()
        {
            foreach(FGrassSector grassSector in grassSectors)
            {
                grassSector.Init(boundComponent.boundSector);
            }
        }
        
        private void ReleaseGrassSectors()
        {
            foreach (FGrassSector grassSector in grassSectors)
            {
                grassSector.Release();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void InitViewFoliage(in float3 viewPos, in float4x4 matrixProj, FPlane* planes, in NativeList<JobHandle> taskHandles)
        {
            if(!needUpdateGPU) { return; }

            foreach (FGrassSector grassSector in grassSectors)
            {
                grassSector.BuildInstance(boundComponent.SectionSize, terrain.detailObjectDensity, taskHandles);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void DispatchSetup(in NativeList<JobHandle> taskHandles)
        {

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetGPUData(CommandBuffer cmdBuffer)
        {
            if (!needUpdateGPU) { return; }
            lastDensityScale = terrain.detailObjectDensity;

            foreach (FGrassSector grassSector in grassSectors)
            {
                grassSector.SetGPUData(cmdBuffer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void DispatchDraw(CommandBuffer cmdBuffer, in int passIndex)
        {
            foreach (FGrassSector grassSector in grassSectors)
            {
                grassSector.DispatchDraw(cmdBuffer, passIndex);
            }
        }
        #endregion //Grass
    }
}
