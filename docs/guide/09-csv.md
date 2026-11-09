# 9. CSV import and export

[Knowledge base](README.md) › CSV import and export

Both are in the `...` menu above the file list.

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
