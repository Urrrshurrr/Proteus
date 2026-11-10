# 2. Adding and managing files

[Knowledge base](README.md) › Adding and managing files

## Ways to add files

| Method | How |
|---|---|
| Drag and drop | Drag files and/or folders from File Explorer onto the list. The list outline lights up while you drag. |
| Add files | Click **Add files** (or press **Ctrl+O**) and pick one or more files. |
| Add folder | Click **Add folder** and pick a folder. Every file inside is added. |
| Command line | `Proteus.exe "C:\path\one" "C:\path\two"`, or drop files onto `Proteus.exe` itself. |
| CSV | Import a rename list (see [Names from a folder, a style or a CSV](09-csv.md)). |

Files already in the list are never added twice. Files are added in natural order, so `file2` comes before `file10`.

### Folder options (the `...` menu)

| Option | Effect |
|---|---|
| **Include files in subfolders** | When adding a folder, also add files in all of its subfolders (on by default). |
| **Add folders themselves, to rename them** | Add the folder as one item so you can rename the *folder*, not its contents. |

> When adding a folder, files and folders marked *Hidden* or *System* by Windows are skipped, and so
> are folder links (junctions and symbolic links), so a link can never pull in files from somewhere
> else. Adding a big folder shows **Looking for files…**; close that message to stop the scan.

## Including and excluding files

- Each row has a **checkbox**. Untick it to leave that file out of the batch. Excluded rows fade
  and show *Skipped*.
- Select several rows and press **Space** to toggle them all.
- The `...` menu has **Include all**, **Include none** and **Invert**.

Excluded files don't count for numbering: the next included file gets the next number.

## Removing files from the list

- Select rows and press **Delete**, or click the **–** button in the toolbar.
- The **bin** button in the toolbar clears the whole list.

Removing files from the list never touches the files on disk.

## Sorting

Click a column header (**Current name**, **New name**, **Status**, **Folder**) to sort. Click again to reverse.

> **Sorting changes the order files are numbered in.** If you use Numbering, a list counter or
> `<n>`, sort the list the way you want the numbers to run before you rename.

## Filtering

- Type in **Filter by name** to show only rows whose current or new name contains that text.
- Turn on **Changes only** to hide rows that won't change.

Filtering only changes what you see. Rows the filter hides are still part of the batch if they are ticked.

## Reading a row

| Column | Meaning |
|---|---|
| Checkbox | Included in the batch or not |
| Icon | File or folder |
| **Current name** | Today's name. Characters that will be removed are shown in red with a strike-through. |
| **New name** | The name after all rules. New characters are highlighted green. |
| **Status** | *Rename*, *Copy*, *Move*, *Update*, *No change*, *Skipped*, *Conflict* or *Invalid*. See [Safety and status messages](11-safety.md). |
| **Folder** | The file's folder, or `→ destination` when copying or moving. |

Hover a row for its full path, the reason for a problem, and any planned date or attribute changes.
Turn off **Highlight changed characters** in the `...` menu if you prefer plain text.

## The Step by step panel

Select a file to see its name after each rule:

```
0  Original     IMG_2041.JPG
1  Clean up     IMG 2041.JPG
2  Replace      Holiday 2041.JPG
3  Change case  Holiday 2041.JPG     (faded: this rule changed nothing)
```

When copying or moving, the destination folder is listed too, and so are any date and attribute changes.
It's the quickest way to find out which rule is doing something unexpected.

---

← [Getting started](01-getting-started.md) · Next: [Working with rules](03-rules.md) →
