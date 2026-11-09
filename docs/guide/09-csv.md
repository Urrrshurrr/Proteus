# 9. Names from a folder or a CSV

[Knowledge base](README.md) › Names from a folder or a CSV

Sometimes the new names already exist somewhere else: in another folder, or in a spreadsheet.
Everything on this page is in the `...` menu above the file list.

## Match names from a folder

Gives the files in your list the **exact names of matching files in another folder**. Each file
keeps its own extension.

The typical case: your media server (Jellyfin, Plex, Emby, Kodi) already has a TV series named and
organised the way you like, and you have better quality copies of the same episodes with messy
names. Match them to the library folder, rename, and the new files can replace the old ones
without anything else noticing the difference.

### 1. Add the files to rename

Add the files that should get the new names (the better copies), as usual. Folders in the list are
ignored.

### 2. Choose the folder with the names

`...` → **Match names from a folder…** opens a window. **Browse…** to the folder whose names you
want to copy, or paste its path.

| Option | What it does |
|---|---|
| **Include subfolders** | Also reads names from subfolders such as `Season 01`, `Season 02`. On by default. |
| **Only names of these file types** | Which files in that folder count. When your list holds videos, this starts as the common video types (`*.mkv;*.mp4;…`), so posters, subtitles and `.nfo` files are skipped. Use `*` for everything. |
| **Turn off my other rules** | Shown when other rules are switched on. Leave it ticked so nothing changes the names after they are matched. |

Trailers, samples and bonus clips (`-trailer`, `-sample`, `extras\`, `featurettes\` and similar) are
never used as names. When several files share a name (`Episode.mkv`, `Episode.nfo`, `Episode.srt`),
the name is offered once.

### 3. Check the pairs

Every file in your list gets a row: its current name on the left, the name it will get on the right.

Proteus pairs them like this:

1. **By season and episode number.** `S01E03`, `s1e3`, `S01.E03`, `1x03`, `01x03`,
   `Season 1 Episode 3` and double episodes such as `S01E01E02` or `S01E01-E02` are all understood,
   so `Show.S01E03.mkv` pairs with `Show - 01x03 - Title.mkv`. Different episode numbers never pair.
2. **By how alike the names are**, when there is no episode number (films, for example). Release
   details such as `1080p`, `x265`, `BluRay`, `WEB-DL` or `DDP5.1` are ignored, and so are dots,
   underscores, accents and capitals.

Each name is used only once: the clearest pairs are made first, so a weaker guess can't take a name
another file matches exactly.

The **Match** column tells you how sure the pairing is:

| Label | Meaning |
|---|---|
| **Episode** (green) | Same season and episode number. |
| **Similar** / **Weak** (amber) | Paired by resemblance. Worth a look, especially *Weak*. |
| **Your pick** | You chose the name yourself. |
| **Same name** | The file already has this name; it is left alone. |
| **No match** | Nothing was close enough. |
| **Used twice** (red) | Two files have been given the same name. Fix one of them. |

To change a pair, pick another name from the drop-down (type the first letters to jump). Pick
*Leave this file as it is*, or untick the row, to skip a file.

### 4. Use the names

Press **Use … names**. Proteus adds a **List of names** rule in *mapping* mode with one line per
file (full path → new name), turns off your other rules if you asked it to, and closes the window.

Nothing is renamed yet. You see the normal preview, where conflicts are flagged as usual, and press
**Rename** when it looks right. Like every batch, it can be undone from History.

### Tips

- To put the renamed files straight into the library, use [Copy or move](07-copy-move.md) only if
  the old files have already been removed. Proteus never overwrites an existing file, so a clash
  is flagged instead. Most people rename in place and then copy the files over in Explorer, choosing
  *Replace the files in the destination*.
- The library folder is only read. Nothing in it is changed.
- Want to change a few names by hand after matching? Edit the lines of the **List of names** rule.

## Import a rename list

Use this when the new names come from a spreadsheet, a database export or another program.

### 1. Prepare the file

Two columns: **the current file** and **its new name**. A header row is optional and is detected automatically.

```csv
Current,New
D:\Scans\scan001.pdf,Contract - Acme 2024.pdf
D:\Scans\scan002.pdf,Invoice 1042.pdf
scan003.pdf,Receipt - Hardware store
```

- The current file can be a **full path**, or a **name relative to the CSV's own folder**
  (`scan003.pdf` above is looked for next to the CSV file).
- The new name may include the extension or not. With *Keep each file's extension* on (the default),
  the extension is kept either way.
- The separator can be a comma, semicolon or tab, so you can save as CSV from Excel in any region or paste tab-separated text.
- Quotes work as usual for names containing commas: `"Smith, John.pdf"`.
- Only the file-name part of a new name is used. To put files into other folders, combine with [Move](07-copy-move.md).

### 2. Import it

`...` → **Import a rename list (CSV)…** → choose the file.

Proteus then:
1. adds every file it can find to the list, and
2. creates a **List of names** rule in *mapping* mode containing your rows.

Nothing is renamed yet. You see the usual preview, can add more rules (for example *Clean up* after
the mapping), fix conflicts, and then press **Rename**.

### Tips

- Rows whose file can't be found still go into the mapping. If you add that file later, it is matched by name.
- Spreadsheet formulas make powerful lists, e.g. `=A2 & " - " & TEXT(B2,"yyyy-mm-dd")`.
- Tokens work in the new names (`Invoice <n:3>`).

## Export the preview

`...` → **Export the preview to CSV…** saves the current plan for review, sign-off or record keeping:

| Column | Content |
|---|---|
| Current path | Full path today |
| New path | Full path after the batch (including destination folder for copy/move) |
| Action | Rename, Move, Copy or Update |
| Status | Ok, Unchanged, Conflict, Invalid, Excluded, Update |
| Dates & attributes | Planned date/attribute changes |
| Message | The reason for any problem |

The file is UTF-8, so Excel shows accented and non-Latin names correctly.

A round trip works well: export, edit the *New path* column in Excel, then import the two columns back.

---

← [Dates and attributes](08-dates-attributes.md) · Next: [Presets, history and undo](10-presets-history.md) →
