# AGENTS.md

This repository is intended to stay close to upstream while carrying a private
Chinese localization branch.

## Branches

- `main`: track upstream `Etheirys/Brio` as cleanly as possible. Avoid local
  feature work here except upstream sync commits.
- `i18n`: localization infrastructure only. Keep this branch language-neutral:
  English remains the default, and other languages are optional overlays.
- `zh-CN`: Chinese resource files and UI wiring for local use. Merge or
  cherry-pick from `i18n` and selected feature branches as needed.
- Bug fixes and small features should branch from clean `main` first. Prepare
  those changes so they can be offered upstream without Chinese resources mixed
  in, then merge or cherry-pick them back into `zh-CN`.

## Localization

- Prefer `Localize.Get("ui.some.key", "English fallback")` for structured UI
  strings and `Localize.Text("English label###stable_id")` for direct ImGui
  labels. `Localize.Text` preserves the `##`/`###` ID suffix automatically.
- Put Simplified Chinese text in
  `Brio/Resources/Embedded/Language/zh-CN.json`.
- Do not hard-code Chinese strings in `.cs` files unless there is no practical
  resource path for that specific value.
- Keep ImGui IDs stable by appending `###stable_id` after localized labels.
- Make localization commits separate from behavior changes whenever possible.

## Validation

- Validate JSON after editing language files.
- Build with the local .NET SDK before packaging when available.
- For upstreamable fixes, test from a branch based on `main` and avoid bringing
  in `zh-CN` resource changes.

## Current State

- `i18n` adds language overlay loading and maps Dalamud Simplified Chinese
  client language to `zh-CN`.
- `zh-CN` contains the Brio 0.8 UI localization pass, including the catalog,
  world-object categories, settings, actor/camera/light controls, posing,
  environment controls, timelines, scene tools, and metadata tools.
