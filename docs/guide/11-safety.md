# 11. Safety and status messages

[Knowledge base](README.md) › Safety and status messages

## Statuses

| Status | Meaning | Will it be changed? |
|---|---|---|
| **Rename** / **Move** / **Copy** | Everything checks out | Yes |
| **Update** | Same name and place; only dates or attributes change | Yes |
| **No change** | The rules don't change this file | No |
| **Skipped** | You unticked it | No |
| **Conflict** | It would clash with another file (see below) | No |
| **Invalid** | The new name or destination isn't allowed, or a rule failed | No |

Hover a *Conflict* or *Invalid* row to read the exact reason. The bottom bar counts how many items
*need attention*, and **Changes only** helps you find them.

## Conflicts

| Message | What to do |
|---|---|
| *Another file would get the same name* | Add a counter (`<n>`), or choose **Add (2), (3)…** in the mode menu |
| *A file with this name already exists* | Same as above, or pick another destination |
| *Would overwrite a file that is not being renamed* | Another file in the plan can't move out of the way, so this one is blocked too. Fix the other file first. |
| *A copy needs a different name or folder* | In copy mode, change the name or pick a destination |
| *Its folder is also being renamed in this batch* | Rename the folder and its contents in two separate batches |

## Invalid names

Proteus checks every new name against Windows' rules before anything happens:

- Not empty, and at most 255 characters.
- None of `\ / : * ? " < > |` and no control characters.
- Doesn't end with a dot or a space.
- Not a reserved device name: `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9` (with any extension).
- Destination folders must be valid paths and must not be existing *files*.

## What Proteus does to keep files safe

- **Nothing happens until you press the button.** The preview is the exact plan.
- **The preview is always current.** If you change something and press Rename straight away, Proteus waits for the new preview first.
- **No overwriting, ever.** Every rename, move and copy refuses to replace an existing file.
- **Swaps and cycles work.** `a → b` and `b → a` in one batch, shifting numbers up by one, or
  changing only the case (`photo.JPG` → `photo.jpg`) all go safely through temporary names.
- **One failure doesn't stop the rest.** If a file is locked or access is denied, it's reported and the others continue.
- **Everything is recorded first.** The plan is written to History before the first file is touched, so even a crash can be undone.
- **Undo is careful.** It only moves back files that are still where Proteus put it, and it never permanently deletes a copy.
- **Sandboxed scripts.** JavaScript rules can't touch files or the network.
- **Long paths** (over 260 characters) are supported.

## When something goes wrong

Unexpected errors are shown in the message bar and written to a log file, `proteus.log` (see
[where things are stored](12-settings-portable.md#where-things-are-stored)). Include it when you report a problem.

---

← [Presets, history and undo](10-presets-history.md) · Next: [Settings and portable mode](12-settings-portable.md) →
