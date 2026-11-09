# 14. FAQ and troubleshooting

[Knowledge base](README.md) › FAQ and troubleshooting

## Keyboard shortcuts

| Keys | Action |
|---|---|
| **Ctrl+O** | Add files |
| **Ctrl+Enter** | Rename / Copy / Move (the main button) |
| **Ctrl+Z** | Undo the last batch (when not typing in a text box) |
| **Delete** | Remove selected files from the list |
| **Space** | Tick / untick selected files |
| **Esc** | Close the History panel |

## Questions

**Is it really safe to try things?**
Yes. Nothing on disk changes until you press the main button, every batch can be undone from
History, and Proteus never overwrites a file.

**Why is a file marked *Invalid*?**
Hover the row. Usually the new name has a character Windows forbids (`: ? * " < > | / \`), ends with a
dot or space, is a reserved name like `CON`, or a rule (often a script) failed. See [Safety](11-safety.md).

**Why is a file marked *Conflict*?**
Two files would end up with the same name, or the name is already taken. Add a counter, or choose
**Add (2), (3)…** in the mode menu. See [Conflicts](11-safety.md#conflicts).

**My numbers are in the wrong order.**
Numbering follows the list order. Click a column header to sort, then check *Step by step*.

**A metadata token comes out empty.**
That file doesn't have that piece of information (photos edited by some apps lose EXIF; many
documents have no title). Check the file's *Properties → Details* in Explorer. For a fallback value,
use a [JavaScript](06-javascript.md) rule with `token('...') || something`.

**`<video:resolution>` is empty for some videos.**
Proteus reads MKV and WebM itself and uses Windows for the rest. If Windows can't read a format, no
resolution is available. Installing a codec pack that adds Windows property handlers helps.

**Do I need Microsoft Office or Acrobat for document tokens?**
No. Proteus reads Word, Excel, PowerPoint, OpenDocument and PDF properties directly.

**Can I rename folders?**
Yes. In the `...` menu tick **Add folders themselves, to rename them**, then add the folders.
Renaming a folder *and* files inside it in the same batch isn't allowed; do them in two batches.

**Can I undo a rename after closing Proteus?**
Yes. Open **History** and click **Undo** on that batch.

**Undo says a file "is no longer where Proteus put it".**
It was moved, renamed or deleted outside Proteus after the batch. Proteus leaves it alone rather
than guessing.

**Undo left my copies in place.**
Either the copy was edited after it was made, or it's on a network/USB drive with no Recycle Bin.
Proteus never deletes those permanently, so delete them yourself if you want them gone.

**Proteus says a batch was interrupted.**
It was closed or the PC stopped mid-batch. Open **History** and **Undo** the interrupted batch to
put everything back, then run it again.

**Where are my settings, presets and history?**
See [Where things are stored](12-settings-portable.md#where-things-are-stored).

**How do I run it from a USB stick?**
Use [portable mode](12-settings-portable.md#portable-mode): add an empty `portable.txt` next to the exe.

**Can scripts damage my files?**
No. Scripts run in a sandbox and can only compute names. See [limits and safety](06-javascript.md#limits-and-safety).

**Is there a command-line or Explorer right-click mode?**
Not yet. You can pass files on the command line to open them in Proteus (`Proteus.exe "D:\folder"`).

**Something went wrong. What do I send?**
The message shown at the bottom, plus `proteus.log` from the
[data folder](12-settings-portable.md#where-things-are-stored). Open an issue on
[GitHub](https://github.com/Urrrshurrr/Proteus/issues).

---

← [Recipes](13-recipes.md) · [Back to the knowledge base](README.md)
