# Dockyard polish, November 2026

Dockyard in the byhofman restyle round. The owner's direction: "Polish, the same as Vaultbar got. Keep Dockyard's
layout, type and components." Audit first, with an honest score out of 5 per part. The dashboard would be redesigned
only if it scored clearly below 3.5. It did not, so all three parts got a polish only. The changes are these:

- the logo's colours are clearly the accent;
- selection is a wash and weight, with no outline;
- text passes WCAG AA in light and dark, and panels stand clear in dark;
- spacing is one gutter per pane, with no clipping and a visible keyboard focus;
- visual bugs are fixed;
- every colour is a theme token, and a test guards that.

Behaviour is unchanged. This closes DOK-27.

## How the shots were made

The shots are in [`restyle-2026-11/before/`](restyle-2026-11/before/) and
[`restyle-2026-11/after/`](restyle-2026-11/after/). They are named `<part>-<nn>-<state>-<light|dark>.png`. The
before shots are of `main` at `33aa84d`.

These are **not** `screencapture` shots. The session had no Screen Recording permission. The real windows are
rendered by Avalonia's headless Skia backend with `CaptureRenderedFrame`. That is the same route as the dashboard's
`PrototypeScreens` (decision 2026-11-17). The harness lives outside the repository, in the session's scratch folder.
It drives the real `MainWindow`s, view models and `App.axaml` over fakes:

- **Dashboard:** `ShellViewModel` is given an in-memory Docker engine, an `HttpMessageHandler` that answers the API
  from fixtures (containers, a compose project, a kind cluster, images, volumes, networks, `system/df`, inspect and
  logs). It never opens a socket. `EngineSession.Start` is never called, so no Docker context is read.
- **Wizard and tray window:** `MainViewModel` is given a fake `IEngineHost` with the real macOS and Windows component
  catalogues. Its install holds on a task, which gives the "installing" state. Settings go to a throwaway file.
- The process runs with `HOME` in a throwaway folder and refuses to start otherwise. Nothing touches Docker, an
  engine, a VM, WSL, `~/.docker` or the docker socket.

**The tray menu is the OS's own** (NSMenu, a Win32 menu or StatusNotifier). Avalonia does not draw it, so it cannot
be rendered here, and its labels did not change. The tray part is shown as its window ("Engines and settings…", the
same view as the wizard on an install) and as its icon in every state. The icon is drawn on a light and a dark
menu-bar grey.

**The wizard was dark-only before.** Its before shots exist in dark only. The after shots follow the system, so they
come in both themes.

| Part | # | State |
|---|---|---|
| Wizard | 01 | New machine (macOS catalogue) |
| | 02 | Keyboard focus on Install |
| | 03 | Installing, activity open |
| | 04 | Existing install: manage, one update available |
| Tray | 01 | Tray window, Windows: two engines, Linux active, one update |
| | 02 | The same at the 720 px minimum width, Windows active |
| | 03 | Uninstall confirmation |
| | 04 | The tray icon: running, stopped, busy, and with an engine badge |
| Dashboard | 01 | Containers: a compose project, kind nodes, a very long name |
| | 02, 03, 03b | Container detail: logs, inspect, stats |
| | 04 | Compose project detail |
| | 05 | Keyboard focus on a detail button |
| | 06–10 | Images, Volumes, Networks, Clusters, Disk, each with a selection |
| | 11 | Narrow window (820 px) with a long-named container open |
| | 12, 13 | Empty engine; engine not running |

## Before: scores and critique

| Part | Score | Honest critique |
|---|---|---|
| **Wizard** | **3.0** | Dark only, whatever the system theme. The active engine card was marked by an accent **outline**. The pills used hard-coded colours, and their "active context" blue (`#16285A`/`#8FB2FF`) was not the logo's. One error line used a literal `#C4314B`. Placeholders were drawn at half opacity, below AA. The two engine cards each had a 10 px right margin, so their right edge sat 10 px inside every other card's. At the minimum width, component names such as "Docker Engine (Linux, WSL)" were clipped without wrapping. |
| **Tray** | **3.0** | The window is the wizard's view, with all of the above. The running icon was Docker's blue (`#2496ED`), not the logo's, at 2.8:1 on a light menu bar. The busy amber (`#E8A317`) was 1.9:1 there, below the 3:1 for icons. |
| **Dashboard** | **3.5** | A sound, quiet layout with consistent rows, a soft selection wash and a clear detail pane. That is not "clearly below 3.5", so no redesign. The logo's blue showed only on primary buttons, links and the tab underline. The selected sidebar item was a wash with the same grey text. In dark, panels (`#161B23`) barely stood off the canvas (`#0E1117`, 1.09:1). AA failures: muted text on the selection (4.40 dark, 4.46 light), links on the light canvas (4.43), white on the primary hover (3.79), white on the dark danger fill (3.49), light status pills (ok 3.77, warn 3.80, bad 4.30), dark bad pill (4.36), and placeholders (half opacity). Bugs: the Stats tab's memory line showed the CPU percentage too. The volume detail's title had `Classes="title"` on a `SelectableTextBlock`, which the `TextBlock.title` style never matched, so it was plain text. The compose row's `Classes.running` had no style. The sparkline's default colour was a literal `DodgerBlue`. The detail panes used an 18 px gutter against the list's 16 px, and the logs sat 4 px off the tabs. |

## What changed

All colours are tokens in the two `App.axaml` files, and both apps define the same token set in a Light and a Dark
dictionary. No view has a colour literal.

| What | Change |
|---|---|
| **Logo colours** | The accent stays `#1B74C9`, the deep stop of the container's gradient. Dark links and brand icons take the light stop, `#5DB7FF`. The **selected sidebar item** takes the crane's colour (`#8CCAF7` in dark, the light icon's `#1B5E9E` in light) and semibold, on a brand-tinted wash. A **running compose project** shows its icon in the brand blue. The CPU sparkline is the brand blue. The wizard's "active context" pill takes the crane colours. The **tray icon** runs in `#2E86DA`, a blue on the container's own gradient, at 3.4:1 on a light and 3.6:1 on a dark menu bar. Busy is deepened to `#B8800E` (3.1 and 4.0). |
| **Selection** | No outline anywhere. The wizard's active engine card loses its accent border for the selection wash (`#E2EEFB` light, `#18375A` dark). Hovering a selected row keeps the wash instead of Fluent's stronger accent, and an unselected row's hover is a neutral `Hover` token. Hover and selection never look alike. |
| **AA text** | Light muted `#5F6B7D` → `#566275` (6.2:1 on white, 5.3 on the wash). Dark muted `#8A94A6` → `#98A2B3` (6.2 on the panel, 4.7 on the wash). Light links `#1B74C9` → `#1663B0` (5.6 on the canvas). The primary hover goes from lighter to darker (`#1765B0`, 6.0 with white). The danger fill is `#C9302C` in both themes (5.3 with white). The status pills are re-tuned to ≥4.6. Placeholders are drawn at full opacity in the muted token. |
| **Dark lift** | Canvas `#0F1218`, panels `#1C222C` (1.17:1, was 1.09), raised `#262D3A`, lines `#353F51` (1.8:1 against the canvas, was 1.4). Inputs are an inset `#12161D` with a lit `#3A4456` edge and a `#5DB7FF` focus edge. Flyouts and the Expander take the panel tokens instead of Fluent's neutral grey. |
| **Light theme for the wizard and tray window** | They follow the system, as the dashboard already did, with the same tokens. Every `StaticResource` colour became a `DynamicResource`. |
| **Spacing** | Dashboard detail panes use the list's 16 px gutter: titles, tabs, the log toolbar, log lines, inspect and stats. Volume and image key-value rows get 4 px between them. In the wizard, the engine cards use `ColumnSpacing` and line up with the cards below them. Component names and option labels wrap instead of clipping. Keyboard focus keeps Fluent's two-tone ring, visible in both themes (wizard 02, dashboard 05). |
| **Visual bugs** | Stats shows memory alone (`ContainerItem.MemoryText`). `title` and `small` now also style a `SelectableTextBlock`. The compose `running` class is styled. The sparkline has no colour default. The literal `#C4314B` is the `Bad` token. |
| **Tests** | `BrandingTests` (dashboard tests) covers both apps. (1) The accent is the logo's deep stop. Dark links and brand are the light stop. The selected text is each icon's crane colour. (2) Both apps follow the system and define the same keys in Light and Dark. (3) Text, muted, links, bad, on-accent, the selected text, the accent pill, status pills and placeholders pass 4.5:1 on every surface they sit on, in both themes. (4) Brand, ok and muted pass 3:1 as icons and dots. (5) No `.axaml` under `src/` has a colour literal outside the token declarations. `TrayIconTests` (wizard tests) checks that the running blue lies between the gradient stops, and that every tray state is ≥3:1 on a light and a dark bar. The tray colours moved to `TrayColors` so the test needs no render platform. Against `main`'s theme files, 13 of the 16 branding cases fail. |

Checked on every after shot, in both themes. No text is clipped: long names end in an ellipsis, and component names
wrap at 720 px. The detail panes share one left edge. Focus is visible.

## After: scores

| Part | Before | After | Why not higher |
|---|---|---|---|
| Wizard | 3.0 | 4.0 | The form is still a long single column; the header's dark logo tile merges with the dark header (the mark itself reads). |
| Tray | 3.0 | 4.0 | The menu is the OS's and plain by design. On macOS a monochrome template icon would be more native than a coloured one. |
| Dashboard | 3.5 | 4.0 | Layout and components are unchanged by direction. The Disk view leaves the right half of a wide window empty, and its filled red "Clean everything unused" is loud for a header action. |

## For the owner

1. **The wizard and tray window follow the system theme now.** They were dark-only. The other option is to keep
   them dark and let only the dashboard follow the system. The shots show both themes.
2. **How far the logo goes.** Shipped: accent, links, the selected item's text and icon in the crane colour, running
   compose icons, the active-context pill and the tray icon. The other option is to keep the selected item's text
   neutral and use colour only for the wash.
3. **The tray icon.** It stays coloured, now in the logo's gradient blue. On macOS a monochrome template image
   (which the OS tints) is the more native alternative. It would lose the running, stopped and busy colours, so the
   state would need a badge.
4. **The dark header logo.** The icon's dark tile merges with the dark header, so the crane and the container float.
   The option is a 1 px ring or the light tile in dark mode. Left as is.
5. **No dashboard redesign.** It scored 3.5, not clearly below. The two candidates for a later design pass are the
   Disk view's empty right half and its loud clean-up button.
6. **Real captures.** To swap these renders for `screencapture` shots, give the terminal Screen Recording
   permission. The tray menu can only be captured that way.
