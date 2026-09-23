# Eternals Level Management

Reusable Unity level-flow and Addressables support.

Copy `Packages/com.eternals.level-management` into another Unity project, or publish the folder as a Git UPM package. The target project must use Addressables `1.22.3` or a compatible version.

## Runtime integration

Derive the game controller from `LevelManagerBase`. Implement `LoadLevel`, `RestartLevel`, and the protected win/lose hooks while keeping board creation, progress, UI, rewards, and analytics in the game project. `LoadNextLevel` wraps to level zero using the current dynamic `LevelCount`.

Derive the game's catalog asset from `LevelDataManagerBase<TLevel>`. The package owns the serialized `Use Addressables`, `Discover Addressables At Runtime`, `Addressable Label`, and ordered remote-reference settings, plus catalog initialization and remote loading. Every derived manager receives those Inspector settings automatically.

Implement `LocalLevelCount` and `GetLevelData` using the player-built local list. The inherited `GetLocalFallbackLevelData` wraps an unavailable remote index over that local list and skips null entries.

Use `AddressableLevelCatalog<TLevel>` to discover `TLevel` assets by label. The catalog only loads resource locations during initialization; each level asset is loaded on demand. The caller owns loaded asset handles and must release them.

`AddressableLevelCatalog<TLevel>` is composed by the package base class. The game-specific manager does not need to duplicate catalog state, serialized remote references, initialization, or async remote-load code.

## Editor integration

Call `AddressableLevelRegistrationUtility.TryGetChangeCount` before confirmation, then `Apply` to register or unregister the game's level GUIDs. It validates bundled groups and asset paths, assigns the discovery label, records Undo, marks Addressables settings dirty, and saves the assets.

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
