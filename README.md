# Eternals Level Management

Reusable Unity level-flow and Addressables support.

Copy `Packages/com.eternals.level-management` into another Unity project, or publish the folder as a Git UPM package. The target project must use Addressables `1.22.3` or a compatible version.

## Runtime integration

Derive the game controller from `LevelManagerBase`. Implement `LoadLevel`, `RestartLevel`, and the protected win/lose hooks while keeping board creation, progress, UI, rewards, and analytics in the game project. `LoadNextLevel` wraps to level zero using the current dynamic `LevelCount`.

Derive the game's catalog asset from `LevelDataManagerBase<TLevel>`. The package owns the serialized `Use Addressables`, `Discover Addressables At Runtime`, `Addressable Label`, ordered remote-reference settings, local and Addressable folder paths, `Max Level`, and selected remote group GUID. Catalog initialization and remote loading are also package-owned.

`LevelDataManagerBase<TLevel>` owns the serialized local `levels` list and provides `Levels`, `LocalLevelCount`, `GetLevelData`, `SetLocalLevels`, and `AddLocalLevel`. Override those members only when a game needs a custom local-list representation. The inherited `GetLocalFallbackLevelData` wraps an unavailable remote index over the local list and skips null entries.

For A/B level sets, configure the `LevelDataOption[]` array with a unique Addressables label, a local folder, and a remote-copy folder for each option. **Update Local Level List** includes each option's local levels in the player build; **Create / Sync Remote Addressable Copies** copies each option set and applies its label. Set `priorityOption` to `main` or an option label. Before each load, `AddressableLevelLoader<TLevel>` calls `PrepareLevelOptionForLoad()`, which calls the overridable `GetLevelOptionForLoad()` hook. Override that hook or call `SetPriorityOption()` from the game's experiment assignment to select a player bucket. `CurrentLevelOption` reports the resolved option and `CurrentLevelSource` reports whether the last successful load came from Addressables or local data.

When an option is selected, the loader tries its remote label, then the main remote catalog; if neither level can be loaded, it tries that option's local list and then the main local list. The level count is the larger of the main and selected-option catalogs, so an option can add levels without expanding the main bucket. Option remote catalogs use runtime label discovery, so keep `Discover Addressables At Runtime` enabled when remote A/B levels are required.

Use `AddressableLevelCatalog<TLevel>` to discover `TLevel` assets by label. The catalog only loads resource locations during initialization; each level asset is loaded on demand. The caller owns loaded asset handles and must release them.

Use `AddressableLevelLoader<TLevel>` to keep async remote loading, local fallback, request cancellation, and Addressables handle ownership out of the game-specific `LevelManager`. Provide callbacks for applying loaded data and handling the case where both remote and local data are unavailable. Dispose the loader with its owning game manager.

`AddressableLevelCatalog<TLevel>` is composed by the package base class. The game-specific manager does not need to duplicate catalog state, serialized remote references, initialization, or async remote-load code.

## Editor integration

The package automatically supplies a custom Inspector for every asset derived from `LevelDataManagerSettingsBase`. It draws the serialized catalog fields and shared folder/max-level/group settings, then provides **Update Local Level List**, **Create / Sync Remote Addressable Copies**, and remote-registration controls. No game-specific custom Inspector is required.

Game-specific managers can implement `ILevelDataManagerValidator` to show a validation button in that Inspector without adding an Odin dependency to the plugin. `LevelDataManagerEditorUtility` calls `AddressableLevelRegistrationUtility` internally. For custom workflows, call `TryGetChangeCount` before confirmation, then `Apply` to register or unregister level GUIDs. It validates bundled groups and asset paths, assigns the discovery label, records Undo, marks Addressables settings dirty, and saves the assets.

Use `AddressableLevelCopyUtility.CreateOrUpdateCopies<TLevel>` when local and remote levels must be separate assets. Pass the ordered local level list, a dedicated destination folder, the remote Addressables group, and the discovery label. The utility:

- leaves every local source asset unchanged and non-Addressable;
- creates missing remote copies and updates existing copies in place, preserving their GUIDs;
- assigns ordered Addressable addresses so runtime discovery matches the local list order;
- moves the copies to the selected bundled group and applies the discovery label;
- removes that label from local sources and stale copies without deleting their assets;
- records Undo and saves the modified assets and Addressables settings.

If the local folder is `Assets/Game/Levels/LevelMain`, `GetSiblingAddressableFolder` returns `Assets/Game/Levels/LevelMainAddressable` by default.

At runtime, attempt remote catalog discovery and remote asset loading first. If either operation fails, resolve the requested index against the local level list. Local assets must remain directly referenced by a runtime asset or scene so Unity includes them in the player build.

## Content updates

1. Keep the level group on remote build/load paths.
2. Label every level with the configured catalog label.
3. For a released app, run **Update a Previous Build** with the matching `addressables_content_state.bin`.
4. Upload new bundles first, then the remote catalog JSON and hash.
5. Preserve old bundles while released clients may still reference them.

`AddressableLevelRegistrationUtility` provides reusable Editor registration, validation, labels, dirty-state handling, and Undo support.
