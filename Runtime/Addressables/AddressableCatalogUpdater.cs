using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Eternals.LevelManagement.Addressables
{
    /// <summary>
    /// Downloads the newest remote Addressables catalog once per session, before level locations are read.
    /// Without this step Addressables only caches the new catalog and applies it on the next launch.
    /// Waits at most <see cref="TimeoutSeconds"/>; on timeout or failure the current catalog is used.
    /// </summary>
    public static class AddressableCatalogUpdater
    {
        /// <summary>Longest wait for the catalog check/update before falling back to the catalog already loaded.</summary>
        public static float TimeoutSeconds = 5f;

        private static bool finished;
        private static bool running;
        private static readonly List<Action> waiters = new();
        private static Runner runner;

        public static void EnsureUpdated(Action completed)
        {
            if (finished)
            {
                completed?.Invoke();
                return;
            }

            if (completed != null)
                waiters.Add(completed);

            if (running)
                return;

            running = true;
            EnsureRunner().StartCoroutine(Run());
        }

        private static IEnumerator Run()
        {
            bool done = false;
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.5f, TimeoutSeconds);

            AsyncOperationHandle<List<string>> checkHandle =
                UnityEngine.AddressableAssets.Addressables.CheckForCatalogUpdates(false);
            checkHandle.Completed += check =>
            {
                List<string> catalogs = check.Status == AsyncOperationStatus.Succeeded ? check.Result : null;
                if (catalogs == null || catalogs.Count == 0)
                {
                    UnityEngine.AddressableAssets.Addressables.Release(check);
                    done = true;
                    return;
                }

                AsyncOperationHandle<List<UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator>> updateHandle =
                    UnityEngine.AddressableAssets.Addressables.UpdateCatalogs(catalogs, false);
                UnityEngine.AddressableAssets.Addressables.Release(check);
                updateHandle.Completed += update =>
                {
                    UnityEngine.AddressableAssets.Addressables.Release(update);
                    done = true;
                };
            };

            while (!done && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!done)
                Debug.LogWarning("[AddressableCatalogUpdater] Catalog update timed out; using the catalog already loaded.");

            finished = true;
            running = false;
            Action[] callbacks = waiters.ToArray();
            waiters.Clear();
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i]?.Invoke();
        }

        /// <summary>
        /// Finds the active remote catalog so a loaded level can be attributed to the content version that served it.
        /// <paramref name="version"/> is the catalog file name without extension (<c>catalog_&lt;playerVersion&gt;</c>),
        /// <paramref name="hash"/> is the catalog content hash. Returns false when no remote catalog is loaded.
        /// </summary>
        public static bool TryGetActiveCatalog(out string version, out string hash)
        {
            version = null;
            hash = null;

            foreach (UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator locator in
                     UnityEngine.AddressableAssets.Addressables.ResourceLocators)
            {
                UnityEngine.AddressableAssets.ResourceLocatorInfo info =
                    UnityEngine.AddressableAssets.Addressables.GetLocatorInfo(locator);
                if (info == null || info.CatalogLocation == null || string.IsNullOrEmpty(info.LocalHash))
                    continue;

                hash = info.LocalHash;

                // The catalog location itself is the bundled "catalog.json"; the versioned name (catalog_<playerVersion>)
                // only appears on its first dependency, the remote hash file.
                var dependencies = info.CatalogLocation.Dependencies;
                string id = dependencies != null && dependencies.Count > 0
                    ? dependencies[0].InternalId
                    : info.CatalogLocation.InternalId;
                version = System.IO.Path.GetFileNameWithoutExtension(id);
                return true;
            }

            return false;
        }

        private static Runner EnsureRunner()
        {
            if (runner != null)
                return runner;

            var go = new GameObject("AddressableCatalogUpdater") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<Runner>();
            return runner;
        }

        private sealed class Runner : MonoBehaviour { }
    }
}
