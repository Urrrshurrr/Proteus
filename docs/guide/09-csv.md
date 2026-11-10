# 9. Names from a folder, a style or a CSV

[Knowledge base](README.md) › Names from a folder, a style or a CSV

Sometimes the new names already exist somewhere else: in another folder, in a few example names,
or in a spreadsheet.
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
- Two copies of the same episode in your list? Only one can take the name; the other is marked
  **Same episode**. Pick the name on that row instead if it's the copy you want.

## Match a naming style

Use this when there is no folder with the exact names, but you know the **style** you want: show
Proteus a few names written that way, and it rebuilds every file in your list in the same style.

`Silicon.Valley.S01E08.Optimal.Tip-to-Tip.Efficiency.1080p.BluRay.x264-GRP.mkv`
→ `Silicon Valley - 01x08 - Optimal Tip-to-Tip Efficiency.mkv`

### 1. Give it some samples

`...` → **Match a naming style…**. In *Sample names*, add two or three names in the style you want:

- **Add sample files…** picks files (only their names are used),
- **Add from a folder…** reads every episode name in a folder and its subfolders, such as a show
  already in your media library, or
- type or paste names, one per line. Extensions are ignored.

Leading release-group tags (`[SubsPlease] Show - 05`) are skipped, and these numbering styles are
understood: `S01E03`, `1x03`, `01 x 03`, `Season 1 Episode 3`, `Episode 3` and anime-style ` - 05`.

### 2. Check the pattern

Proteus works out the pattern and shows it, for example:

```
<show> - <season:00>x<episode:00> - <title>
```

| Field | Meaning |
|---|---|
| `<show>` | Show or film name |
| `<year>` | Year, e.g. `2014` |
| `<season:00>` | Season number. The number of zeros is the padding: `<season:0>` gives `1`, `<season:00>` gives `01`. |
| `<episode:00>` | Episode number, padded the same way. Double episodes become `01-E02` after an `E`, or `01-02`. |
| `<title>` | Episode title |
| `<quality>` | Resolution, e.g. `1080p` |

Everything else in the pattern (`" - "`, `S`, `E`, `x`, brackets) is used as it is. You can edit the
pattern freely, for example to change `x` into `S…E…`. Click a field under the pattern to insert it
where the cursor is.

It also learns, and lets you change:

- **Words separated by**: spaces, dots, underscores or hyphens.
- **Capitalisation**: as in each file, Title Case, lowercase or UPPERCASE. With Title Case, *Keep small
  words lowercase* writes "Two Days of the Condor" rather than "Two Days Of The Condor"; with it off,
  small words are left as each file has them. Words that are already mixed case ("RussFest") or
  acronyms ("NASA") are kept.
- **Punctuation** in show names and titles is kept when words are separated by spaces:
  `Rome, in a Day`, `Caretaker (1)`, `The Office (US)`. Dotted and underscored styles keep the words only.
- **Show name** and **Year**: leave empty to take them from each file, or type them to use the same
  value for every file (handy when files use an abbreviation, or have no year).

### 3. Check the names

Every file gets a row with the name it will get. You can **type over any name**; a typed name is
kept when you change the style, and the ↶ button brings the styled name back.

| Status | Meaning |
|---|---|
| **Ready** (green) | Everything the pattern needs was found in the file's name. |
| **From sample** (green) | A sample is the same episode of the same show, so its title was copied exactly. |
| **No episode title** / **No year** (amber) | That part isn't in the file's name, so it was left out together with its separator (`Show - 02x01`). Type it in, or leave it. |
| **No episode number** / **No name** | The file's name doesn't say which episode it is, so the file is left as it is. Type a name to rename it anyway. |
| **Used twice** (red) | Two files in the same folder would get the same name. |
| **Edited** | You typed this name. |
| **Same name** | The file already has this name. |

Release details (`1080p`, `x265`, `WEB-DL`, `REPACK`, group tags) are never mistaken for part of a
title. Hover a row to see what Proteus read from the file's name (show, year, episode, title).

**Exact names from your library.** With *Take show names and exact titles from the samples* on,
a show that appears in the samples is written the way they write it (`Greys.Anatomy` →
`Grey's Anatomy`), and when a sample is the same season and episode, its title is copied exactly
too, apostrophes and all (`Greys.Anatomy.S01E01.A.Hard.Days.Night` →
`Grey's Anatomy - 01x01 - A Hard Day's Night`). So adding the whole show from your library with
**Add from a folder…** gives you the style and the official titles in one go. Episodes the samples
don't have still take their title from their own names. Proteus doesn't look titles up online. To match every file to an existing
name, use [Match names from a folder](#match-names-from-a-folder).

### 4. Use the names

Press **Use … names**. As with matching from a folder, this adds a **List of names** rule (and can
turn your other rules off), and you rename from the normal preview.

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
  the file's own extension is always kept: `report.txt` mapped to `Summary` or `Summary.txt` gives
  `Summary.txt`, but mapped to `Summary.md` gives `Summary.md.txt`. To change extensions from the
  list, untick *Keep each file's extension* in the List of names rule and write every new name
  with its extension.
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

The file is UTF-8, so Excel shows accented and non-Latin names correctly. A value that starts with
`=`, `+`, `-` or `@` gets a leading `'`, so Excel shows it as text instead of running it as a formula
(remove the `'` if you import that column back).

A round trip works well: export, edit the *New path* column in Excel, then import the two columns back.

---

← [Dates and attributes](08-dates-attributes.md) · Next: [Presets, history and undo](10-presets-history.md) →
