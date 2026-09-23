using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Eternals.LevelManagement.Addressables
{
    /// <summary>
    /// Discovers a deterministic level list from an Addressables label and
    /// loads individual level assets on demand. Loaded asset handles remain
    /// owned by the caller and must be released by the caller.
    /// </summary>
    public sealed class AddressableLevelCatalog<TLevel> : IDisposable
        where TLevel : UnityEngine.Object
    {
        private readonly string label;
        private readonly List<IResourceLocation> locations = new();

        private AsyncOperationHandle<IList<IResourceLocation>> locationHandle;
        private Action<bool> initializationCompleted;
        private bool isInitializing;
        private bool isInitialized;
        private bool initializationSucceeded;

        public AddressableLevelCatalog(string label)
        {
            this.label = label;
        }

        public string Label => label;
        public bool IsInitializing => isInitializing;
        public bool IsReady => isInitialized;
        public bool Succeeded => initializationSucceeded;
        public int Count => initializationSucceeded ? locations.Count : 0;
        public string LastError { get; private set; }

        public bool Initialize(Action<bool> completed = null)
        {
            if (isInitialized)
            {
                completed?.Invoke(initializationSucceeded);
                return initializationSucceeded;
            }

            if (completed != null)
                initializationCompleted += completed;

            if (isInitializing)
                return true;

            if (string.IsNullOrWhiteSpace(label))
            {
                FinishInitialization(false, "Addressables level label is empty.");
                return false;
            }

            isInitializing = true;
            locationHandle = UnityEngine.AddressableAssets.Addressables.LoadResourceLocationsAsync(
                label,
                typeof(TLevel));
            locationHandle.Completed += OnLocationsLoaded;
            return true;
        }

        public bool TryGetLocation(int index, out IResourceLocation location)
        {
            if (initializationSucceeded && index >= 0 && index < locations.Count)
            {
                location = locations[index];
                return true;
            }

            location = null;
            return false;
        }

        public bool TryLoadLevelAsync(int index, out AsyncOperationHandle<TLevel> handle)
        {
            if (TryGetLocation(index, out IResourceLocation location))
            {
                handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<TLevel>(location);
                return true;
            }

            handle = default;
            return false;
        }

        public void Dispose()
        {
            initializationCompleted = null;
            locations.Clear();
            isInitializing = false;
            isInitialized = false;
            initializationSucceeded = false;

            if (locationHandle.IsValid())
            {
                locationHandle.Completed -= OnLocationsLoaded;
                UnityEngine.AddressableAssets.Addressables.Release(locationHandle);
                locationHandle = default;
            }
        }

        private void OnLocationsLoaded(AsyncOperationHandle<IList<IResourceLocation>> handle)
        {
            if (!locationHandle.IsValid() || !handle.Equals(locationHandle))
                return;

            handle.Completed -= OnLocationsLoaded;
            locations.Clear();

            bool succeeded = handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null;
            if (succeeded)
            {
                for (int i = 0; i < handle.Result.Count; i++)
                {
                    IResourceLocation location = handle.Result[i];
                    if (location != null)
                        locations.Add(location);
                }

                locations.Sort(ResourceLocationComparer.Instance);
                succeeded = locations.Count > 0;
            }

            string error = succeeded
                ? null
                : handle.OperationException != null
                    ? handle.OperationException.Message
                    : $"No {typeof(TLevel).Name} assets were found for label '{label}'.";

            UnityEngine.AddressableAssets.Addressables.Release(handle);
            locationHandle = default;
            FinishInitialization(succeeded, error);
        }

        private void FinishInitialization(bool succeeded, string error)
        {
            isInitializing = false;
            isInitialized = true;
            initializationSucceeded = succeeded;
            LastError = error;

            Action<bool> callbacks = initializationCompleted;
            initializationCompleted = null;
            callbacks?.Invoke(succeeded);
        }

        private sealed class ResourceLocationComparer : IComparer<IResourceLocation>
        {
            public static readonly ResourceLocationComparer Instance = new();

            public int Compare(IResourceLocation left, IResourceLocation right)
            {
                if (ReferenceEquals(left, right))
                    return 0;
                if (left == null)
                    return -1;
                if (right == null)
                    return 1;

                int result = StringComparer.OrdinalIgnoreCase.Compare(left.PrimaryKey, right.PrimaryKey);
                return result != 0
                    ? result
                    : StringComparer.OrdinalIgnoreCase.Compare(left.InternalId, right.InternalId);
            }
        }
    }
}
