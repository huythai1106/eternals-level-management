using System;
using System.Collections.Generic;
using UnityEngine;
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
            // Cập nhật catalog remote trước để lần mở đầu đã thấy level mới.
            AddressableCatalogUpdater.EnsureUpdated(LoadLocations);
            return true;
        }

        private void LoadLocations()
        {
            // Dispose có thể đã chạy trong lúc chờ cập nhật catalog.
            if (!isInitializing)
                return;

            locationHandle = UnityEngine.AddressableAssets.Addressables.LoadResourceLocationsAsync(
                label,
                typeof(TLevel));
            locationHandle.Completed += OnLocationsLoaded;
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

                int result = NaturalLevelNameComparer.Instance.Compare(left.PrimaryKey, right.PrimaryKey);
                return result != 0
                    ? result
                    : NaturalLevelNameComparer.Instance.Compare(left.InternalId, right.InternalId);
            }
        }
    }

    /// <summary>
    /// Loads one level on demand, falls back to the local list when the remote
    /// request fails, and owns the pending and active Addressables handles.
    /// </summary>
    public sealed class AddressableLevelLoader<TLevel> : IDisposable
        where TLevel : UnityEngine.Object
    {
        private readonly LevelDataManagerBase<TLevel> levelDataManager;

        private AsyncOperationHandle<TLevel> pendingHandle;
        private AsyncOperationHandle<TLevel> activeHandle;
        private Action<TLevel, int> levelLoaded;
        private Action<string> loadFailed;
        private int pendingLevelIndex;
        private string pendingPreferredOption = LevelDataManagerSettingsBase.MainLevelOption;
        private string pendingSourceOption = LevelDataManagerSettingsBase.MainLevelOption;
        private bool isDisposed;

        public AddressableLevelLoader(LevelDataManagerBase<TLevel> levelDataManager)
        {
            this.levelDataManager = levelDataManager;
        }

        public bool IsLoading => pendingHandle.IsValid();

        public bool IsActiveAsset(TLevel level)
        {
            return activeHandle.IsValid() && activeHandle.Result == level;
        }

        public bool InitializeCatalog(Action<bool> completed = null)
        {
            if (isDisposed || levelDataManager == null)
            {
                completed?.Invoke(false);
                return false;
            }

            levelDataManager.PrepareLevelOptionForLoad();
            return levelDataManager.InitializeAddressableLevels(completed);
        }

        /// <summary>
        /// Returns true when an asynchronous remote request was accepted or a
        /// local fallback was delivered to the callback.
        /// </summary>
        public bool Load(
            int requestedIndex,
            Action<TLevel, int> onLevelLoaded,
            Action<string> onLoadFailed = null)
        {
            if (isDisposed || levelDataManager == null)
            {
                onLoadFailed?.Invoke("The level loader or its data manager is unavailable.");
                return false;
            }

            CancelPendingLoad();
            string preferredOption = levelDataManager.PrepareLevelOptionForLoad();

            if (!levelDataManager.UseAddressables)
            {
                return LoadLocalLevel(
                    requestedIndex,
                    preferredOption,
                    onLevelLoaded,
                    onLoadFailed,
                    null);
            }

            bool hasPreferredOption = !string.Equals(
                preferredOption,
                LevelDataManagerSettingsBase.MainLevelOption,
                StringComparison.OrdinalIgnoreCase);

            if (hasPreferredOption &&
                levelDataManager.TryLoadOptionAddressableLevelAsync(
                    preferredOption,
                    requestedIndex,
                    out AsyncOperationHandle<TLevel> optionHandle))
            {
                return BeginRemoteLoad(
                    requestedIndex,
                    preferredOption,
                    preferredOption,
                    optionHandle,
                    onLevelLoaded,
                    onLoadFailed);
            }

            if (hasPreferredOption && TryLoadLocalOptionLevel(
                    requestedIndex,
                    preferredOption,
                    onLevelLoaded,
                    "no valid Addressables location or reference was found"))
            {
                return true;
            }

            if (levelDataManager.TryLoadMainAddressableLevelAsync(
                    requestedIndex,
                    out AsyncOperationHandle<TLevel> mainHandle))
            {
                return BeginRemoteLoad(
                    requestedIndex,
                    preferredOption,
                    LevelDataManagerSettingsBase.MainLevelOption,
                    mainHandle,
                    onLevelLoaded,
                    onLoadFailed);
            }

            string missingRemoteReason = hasPreferredOption
                ? "no valid Addressables location or reference was found for the option or main level"
                : "no valid Addressables location or reference was found";
            return LoadLocalLevel(
                requestedIndex,
                LevelDataManagerSettingsBase.MainLevelOption,
                onLevelLoaded,
                onLoadFailed,
                missingRemoteReason);
        }

        public void CancelPendingLoad()
        {
            if (pendingHandle.IsValid())
            {
                pendingHandle.Completed -= OnRemoteLevelLoaded;
                UnityEngine.AddressableAssets.Addressables.Release(pendingHandle);
                pendingHandle = default;
            }

            levelLoaded = null;
            loadFailed = null;
            pendingPreferredOption = LevelDataManagerSettingsBase.MainLevelOption;
            pendingSourceOption = LevelDataManagerSettingsBase.MainLevelOption;
        }

        public void ReleaseActiveAsset()
        {
            if (!activeHandle.IsValid())
                return;

            UnityEngine.AddressableAssets.Addressables.Release(activeHandle);
            activeHandle = default;
        }

        public void ReleaseActiveAssetUnless(TLevel asset)
        {
            if (IsActiveAsset(asset))
                return;

            ReleaseActiveAsset();
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            CancelPendingLoad();
            ReleaseActiveAsset();
        }

        private void OnRemoteLevelLoaded(AsyncOperationHandle<TLevel> handle)
        {
            if (!pendingHandle.IsValid() || !handle.Equals(pendingHandle))
                return;

            handle.Completed -= OnRemoteLevelLoaded;
            int requestedIndex = pendingLevelIndex;
            Action<TLevel, int> onLoaded = levelLoaded;
            Action<string> onFailed = loadFailed;
            pendingHandle = default;
            levelLoaded = null;
            loadFailed = null;

            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                string reason = handle.OperationException != null
                    ? handle.OperationException.Message
                    : "Addressables returned an empty level asset";
                UnityEngine.AddressableAssets.Addressables.Release(handle);

                bool failedPreferredOption = !string.Equals(
                    pendingSourceOption,
                    LevelDataManagerSettingsBase.MainLevelOption,
                    StringComparison.OrdinalIgnoreCase);

                if (failedPreferredOption && TryLoadLocalOptionLevel(
                        requestedIndex,
                        pendingPreferredOption,
                        onLoaded,
                        reason))
                {
                    return;
                }

                if (failedPreferredOption && levelDataManager.TryLoadMainAddressableLevelAsync(
                        requestedIndex,
                        out pendingHandle))
                {
                    pendingSourceOption = LevelDataManagerSettingsBase.MainLevelOption;
                    levelLoaded = onLoaded;
                    loadFailed = onFailed;
                    pendingHandle.Completed += OnRemoteLevelLoaded;
                    return;
                }

                LoadLocalLevel(
                    requestedIndex,
                    LevelDataManagerSettingsBase.MainLevelOption,
                    onLoaded,
                    onFailed,
                    reason);
                return;
            }

            AsyncOperationHandle<TLevel> previousHandle = activeHandle;
            activeHandle = handle;
            levelDataManager.RecordLevelLoadSource(true, pendingSourceOption);
            onLoaded?.Invoke(handle.Result, requestedIndex);
            if (previousHandle.IsValid())
                UnityEngine.AddressableAssets.Addressables.Release(previousHandle);
        }

        private bool BeginRemoteLoad(
            int requestedIndex,
            string preferredOption,
            string sourceOption,
            AsyncOperationHandle<TLevel> handle,
            Action<TLevel, int> onLoaded,
            Action<string> onFailed)
        {
            pendingHandle = handle;
            pendingLevelIndex = requestedIndex;
            pendingPreferredOption = preferredOption;
            pendingSourceOption = sourceOption;
            levelLoaded = onLoaded;
            loadFailed = onFailed;
            pendingHandle.Completed += OnRemoteLevelLoaded;
            return true;
        }

        private bool TryLoadLocalOptionLevel(
            int requestedIndex,
            string option,
            Action<TLevel, int> onLoaded,
            string remoteFailure)
        {
            if (requestedIndex < 0 ||
                requestedIndex >= levelDataManager.GetLocalOptionLevelCount(option))
            {
                return false;
            }

            TLevel localLevel = levelDataManager.GetLocalOptionLevelObject(option, requestedIndex) as TLevel;
            if (localLevel == null)
                return false;

            if (!string.IsNullOrEmpty(remoteFailure))
            {
                Debug.LogWarning(
                    $"Could not load remote level {requestedIndex} for option '{option}' " +
                    $"({remoteFailure}). Loading the local '{option}' level instead.",
                    levelDataManager);
            }

            levelDataManager.RecordLevelLoadSource(false, option, remoteFailure);
            onLoaded?.Invoke(localLevel, requestedIndex);
            ReleaseActiveAsset();
            return true;
        }

        private bool LoadLocalLevel(
            int requestedIndex,
            string preferredOption,
            Action<TLevel, int> onLoaded,
            Action<string> onFailed,
            string remoteFailure)
        {
            if (levelDataManager.UsesAddressableLocalLevels)
            {
                // Local levels are loaded asynchronously in this mode; this loader only supports the synchronous local list.
                string unsupported = $"AddressableLevelLoader cannot deliver local level {requestedIndex} because the data manager uses " +
                                     $"{nameof(LocalLevelStorage)}.{nameof(LocalLevelStorage.Addressables)}. Use TryLoadLocalFallbackLevelAsync instead.";
                Debug.LogError(unsupported, levelDataManager);
                onFailed?.Invoke(unsupported);
                return false;
            }

            TLevel localLevel = levelDataManager.GetLocalFallbackLevelData(
                requestedIndex,
                preferredOption,
                out int localIndex,
                out string sourceOption);

            if (localLevel == null)
            {
                string reason = string.IsNullOrEmpty(remoteFailure)
                    ? $"No local level exists for requested index {requestedIndex}."
                    : $"Could not load remote level {requestedIndex} ({remoteFailure}), and no local fallback level is available.";
                Debug.LogError(reason, levelDataManager);
                onFailed?.Invoke(reason);
                return false;
            }

            if (!string.IsNullOrEmpty(remoteFailure))
            {
                Debug.LogWarning(
                    $"Could not load remote level {requestedIndex} ({remoteFailure}). " +
                    $"Loading local level {localIndex} instead.",
                    levelDataManager);
            }

            levelDataManager.RecordLevelLoadSource(false, sourceOption, remoteFailure);
            onLoaded?.Invoke(localLevel, requestedIndex);
            ReleaseActiveAsset();
            return true;
        }
    }
}
