// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Experimental.UI.BoundsControlTypes;
using UnityEngine;
using System.Collections.Generic;

namespace Microsoft.MixedReality.Toolkit.Experimental.UI.BoundsControl
{
    /// <summary>
    /// Rotation handles for <see cref="BoundsControl"/> that are used for rotating the
    /// Gameobject BoundsControl is attached to with near or far interaction
    /// </summary>
    public abstract class PerAxisHandles : HandlesBase
    {
        protected override HandlesBaseConfiguration BaseConfig => config;
        protected PerAxisHandlesConfiguration config;
        protected Vector3[] HandlePositions { get; private set; }

        protected abstract int NumHandles { get; }
        protected abstract string HandlePositionDescription { get; }
        internal abstract CardinalAxisType[] handleAxes { get; }

        internal PerAxisHandles(PerAxisHandlesConfiguration configuration)
        {
            HandlePositions = new Vector3[NumHandles];

            Debug.Assert(configuration != null, "Can't create " + configuration.ToString() + " without valid configuration");
            config = configuration;
            config.handlesChanged.AddListener(HandlesChanged);
            config.colliderTypeChanged.AddListener(UpdateColliderType);
        }

        ~PerAxisHandles()
        {
            config.handlesChanged.RemoveListener(HandlesChanged);
            config.colliderTypeChanged.RemoveListener(UpdateColliderType);
        }

        private void UpdateColliderType()
        {
            foreach (var handle in handles)
            {
                // remove old colliders
                bool shouldCreateNewCollider = false;
                var oldBoxCollider = handle.GetComponent<BoxCollider>();
                if (oldBoxCollider != null && config.HandlePrefabColliderType == HandlePrefabCollider.Sphere)
                {
                    shouldCreateNewCollider = true;
                    Object.Destroy(oldBoxCollider);
                }

                var oldSphereCollider = handle.GetComponent<SphereCollider>();
                if (oldSphereCollider != null && config.HandlePrefabColliderType == HandlePrefabCollider.Box)
                {
                    shouldCreateNewCollider = true;
                    Object.Destroy(oldSphereCollider);
                }

                if (shouldCreateNewCollider)
                {
                    // attach new collider
                    var handleBounds = VisualUtils.GetMaxBounds(GetVisual(handle).gameObject);
                    var invScale = handleBounds.size.x == 0.0f ? 0.0f : config.HandleSize / handleBounds.size.x;
                    Vector3 colliderSizeScaled = handleBounds.size * invScale;
                    Vector3 colliderCenterScaled = handleBounds.center * invScale;
                    if (config.HandlePrefabColliderType == HandlePrefabCollider.Box)
                    {
                        BoxCollider collider = handle.gameObject.AddComponent<BoxCollider>();
                        collider.size = colliderSizeScaled;
                        collider.center = colliderCenterScaled;
                        collider.size += config.ColliderPadding;
                    }
                    else
                    {
                        SphereCollider sphere = handle.gameObject.AddComponent<SphereCollider>();
                        sphere.center = colliderCenterScaled;
                        sphere.radius = colliderSizeScaled.x * 0.5f;
                        sphere.radius += VisualUtils.GetMaxComponent(config.ColliderPadding);
                    }
                }

            }
        }

        internal int GetHandleIndex(Transform handle)
        {
            for (int i = 0; i < handles.Count; ++i)
            {
                if (handle == handles[i])
                {
                    return i;
                }
            }

            return handles.Count;
        }

        internal Vector3 GetHandlePosition(int index)
        {
            Debug.Assert(index >= 0 && index < NumHandles, "Handle position index out of bounds");
            return HandlePositions[index];
        }

        internal CardinalAxisType GetAxisType(int index)
        {
            Debug.Assert(index >= 0 && index < NumHandles, "Edge axes index out of bounds");
            return handleAxes[index];
        }

        internal CardinalAxisType GetAxisType(Transform handle)
        {
            int index = GetHandleIndex(handle);
            return GetAxisType(index);
        }

        protected void UpdateHandles()
        {
            for (int i = 0; i < handles.Count; ++i)
            {
                handles[i].position = GetHandlePosition(i);
            }
        }

        internal abstract void CalculateHandlePositions(ref Vector3[] boundsCorners);

        internal void Reset(bool areHandlesActive, FlattenModeType flattenAxis)
        {
            IsActive = areHandlesActive;
            ResetHandles();
            if (IsActive)
            {
                List<int> flattenedHandles = VisualUtils.GetFlattenedIndices(flattenAxis, handleAxes);
                if (flattenedHandles != null)
                {
                    for (int i = 0; i < flattenedHandles.Count; ++i)
                    {
                        handles[flattenedHandles[i]].gameObject.SetActive(false);
                    }
                }
            }
        }

        internal void Create(ref Vector3[] boundsCorners, Transform parent)
        {
            HandlePositions = new Vector3[NumHandles];
            CalculateHandlePositions(ref boundsCorners);
            CreateHandles(parent);
        }

        private void CreateHandles(Transform parent)
        {
            for (int i = 0; i < HandlePositions.Length; ++i)
            {
                GameObject handle = new GameObject();
                handle.name = HandlePositionDescription + "_" + i.ToString();
                handle.transform.position = HandlePositions[i];
                handle.transform.parent = parent;

                Bounds midpointBounds = CreateVisual(i, handle);
                float maxDim = VisualUtils.GetMaxComponent(midpointBounds.size);
                float invScale = maxDim == 0.0f ? 0.0f : config.HandleSize / maxDim;
                VisualUtils.AddComponentsToAffordance(handle, new Bounds(midpointBounds.center * invScale, midpointBounds.size * invScale),
                    config.HandlePrefabColliderType, CursorContextInfo.CursorAction.Rotate, config.ColliderPadding, parent, config.DrawTetherWhenManipulating);

                handles.Add(handle.transform);
            }

            VisualUtils.HandleIgnoreCollider(config.HandlesIgnoreCollider, handles);

            objectsChangedEvent.Invoke(this);
        }

        protected override void RecreateVisuals()
        {
            for (int i = 0; i < handles.Count; ++i)
            {
                // get parent of visual
                Transform obsoleteChild = handles[i].Find(visualsName);
                if (obsoleteChild)
                {
                    // get old child and remove it
                    obsoleteChild.parent = null;
                    Object.Destroy(obsoleteChild.gameObject);
                }
                else
                {
                    Debug.LogError("couldn't find rotation visual on recreating visuals");
                }

                // create new visual
                Bounds visualBounds = CreateVisual(i, handles[i].gameObject);

                // update handle collider bounds
                UpdateColliderBounds(handles[i], visualBounds.size);
            }

            objectsChangedEvent.Invoke(this);
        }

        protected override void UpdateColliderBounds(Transform handle, Vector3 visualSize)
        {
            var invScale = visualSize.x == 0.0f ? 0.0f : config.HandleSize / visualSize.x;
            GetVisual(handle).transform.localScale = new Vector3(invScale, invScale, invScale);
            Vector3 colliderSizeScaled = visualSize * invScale;
            if (config.HandlePrefabColliderType == HandlePrefabCollider.Box)
            {
                BoxCollider collider = handle.gameObject.GetComponent<BoxCollider>();
                collider.size = colliderSizeScaled;
                collider.size += BaseConfig.ColliderPadding;
            }
            else
            {
                SphereCollider collider = handle.gameObject.GetComponent<SphereCollider>();
                collider.radius = colliderSizeScaled.x * 0.5f;
                collider.radius += VisualUtils.GetMaxComponent(config.ColliderPadding);
            }
        }

        protected abstract Quaternion GetRotationRealignment(int handleIndex);

        private Bounds CreateVisual(int handleIndex, GameObject parent)
        {
            GameObject midpointVisual;
            GameObject prefabType = config.HandlePrefab;
            if (prefabType != null)
            {
                midpointVisual = Object.Instantiate(prefabType);
            }
            else
            {
                midpointVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                if (config.HandlePrefabColliderType != HandlePrefabCollider.Sphere)
                {
                    Object.Destroy(midpointVisual.GetComponent<SphereCollider>());
                }
            }

            Quaternion realignment = GetRotationRealignment(handleIndex);
            midpointVisual.transform.localRotation = realignment * midpointVisual.transform.localRotation;

            Bounds midpointBounds = VisualUtils.GetMaxBounds(midpointVisual);
            float maxDim = VisualUtils.GetMaxComponent(midpointBounds.size);
            float invScale = maxDim == 0.0f ? 0.0f : config.HandleSize / maxDim;

            midpointVisual.name = visualsName;
            midpointVisual.transform.parent = parent.transform;
            midpointVisual.transform.localScale = new Vector3(invScale, invScale, invScale);
            midpointVisual.transform.localPosition = Vector3.zero;

            if (config.HandleMaterial != null)
            {
                VisualUtils.ApplyMaterialToAllRenderers(midpointVisual, config.HandleMaterial);
            }

            return midpointBounds;
        }

        #region BoundsControlHandlerBase 
        internal override bool IsVisible(Transform handle)
        {
            if (!IsActive)
            {
                return false;
            }
            else
            {
                CardinalAxisType axisType = GetAxisType(handle);
                switch (axisType)
                {
                    case CardinalAxisType.X:
                        return config.ShowHandleForX;
                    case CardinalAxisType.Y:
                        return config.ShowHandleForY;
                    case CardinalAxisType.Z:
                        return config.ShowHandleForZ;
                }

                return false;
            }
        }

        protected override Transform GetVisual(Transform handle)
        {
            // visual is first child 
            Transform childTransform = handle.GetChild(0);
            if (childTransform != null && childTransform.name == visualsName)
            {
                return childTransform;
            }

            return null;
        }
        #endregion BoundsControlHandlerBase

        #region IProximityScaleObjectProvider 
        public override bool IsActive
        {
            get
            {
                return (config.ShowHandleForX || config.ShowHandleForY || config.ShowHandleForZ) && base.IsActive;
            }
        }

        #endregion IProximityScaleObjectProvider

    }
}
