# 4. Rule reference

[Knowledge base](README.md) › Rule reference

All 17 rules, with every option. Examples show **before → after** for the part the rule works on.
Fields marked *tokens* accept [tokens](05-tokens.md) such as `<n:2>` or `<modified:yyyy-MM-dd>`.

| Rule | In one line |
|---|---|
| [Replace](#replace) | Find text or a pattern and replace it |
| [Insert](#insert) | Add text at the start, the end, a position, or next to some text |
| [Remove characters](#remove-characters) | Delete characters by position |
| [Change case](#change-case) | Title Case, Sentence case, lowercase, UPPERCASE, iNVERT |
| [Numbering](#numbering) | Add a running number |
| [New name](#new-name) | Build the whole name from tokens |
| [Clean up](#clean-up) | Tidy separators, brackets, accents, symbols and spaces |
| [Extension](#extension) | Change, remove, append or re-case the extension |
| [Pad numbers](#pad-numbers) | Zero-pad numbers, or remove leading zeros |
| [Rearrange](#rearrange) | Split at a separator and reorder the parts |
| [Trim around text](#trim-around-text) | Remove before, after or between markers |
| [Move](#move) | Move characters or a piece of text elsewhere |
| [Reformat dates](#reformat-dates) | Rewrite dates found in names |
| [Transliterate](#transliterate) | Other alphabets and accents to plain Latin |
| [Randomize](#randomize) | Add random letters or digits |
| [List of names](#list-of-names) | Paste new names, or an old → new mapping |
| [JavaScript](#javascript) | Your own logic in a small script |

---

## Replace

Finds text and replaces it.

| Option | Notes |
|---|---|
| **Find** | The text to look for. |
| **Replace with** (*tokens*) | Leave empty to delete what was found. |
| **Regular expression** | Treat *Find* as a .NET regular expression. Use `$1`, `$2`… in *Replace with* for captured groups. Invalid patterns show a red message. |
| **Match case** | Off by default (`a` also finds `A`). |
| **Which matches** | **All**, **First** or **Last**. |

| Find | Replace with | Options | Before → After |
|---|---|---|---|
| `_` | ` ` | | `my_new_file` → `my new file` |
| `copy` | *(empty)* | | `Report copy` → `Report ` |
| `(\w+) - (\w+)` | `$2 - $1` | Regex | `Artist - Song` → `Song - Artist` |
| `\s+\(\d+\)$` | | Regex | `photo (3)` → `photo` |
| `-` | ` ` | First | `2024-01-05-notes` → `2024 01-05-notes` |

> Outside regex mode, `$` in *Replace with* is just a dollar sign. In regex mode, a `$` that comes
> from a token's value (for example a title like `Cash $$$`) is also kept as text; only the `$` you type
> yourself refers to groups. If a regular expression takes too long on a name, the rule stops for
> the rest of the preview and says so, instead of freezing Proteus.

## Insert

Adds text.

| Option | Notes |
|---|---|
| **Text to insert** (*tokens*) | What to add. |
| **Where** | **At start**, **At end**, **At position**, **Before text**, **After text**. |
| **Position** / **Count from the end** | For *At position*. Position 1 is before the first character. With *Count from the end*, 1 is after the last character. |
| **Before / after this text** | For the text options. Uses the first match; case doesn't matter. |

| Text | Where | Before → After |
|---|---|---|
| `2024 - ` | At start | `Budget` → `2024 - Budget` |
| `_final` | At end | `Report` → `Report_final` |
| `-` | At position 5 | `20240105` → `2024-0105` |
| ` (draft)` | After text `Plan` | `Plan v2` → `Plan (draft) v2` |
| `<modified:yyyy-MM-dd> ` | At start | `notes` → `2024-03-01 notes` |

## Remove characters

Deletes characters by position.

| Option | Notes |
|---|---|
| **How many characters** | Number to delete. |
| **Starting at character** | 1 = from the very first (or last) character. |
| **Count from** | **Start** or **End**. |

| How many | Start at | From | Before → After |
|---|---|---|---|
| 4 | 1 | Start | `IMG_1234` → `1234` |
| 3 | 1 | End | `photo_v02` → `photo_` |
| 2 | 5 | Start | `2024__notes` → `2024notes` |

To delete specific *text* rather than positions, use [Replace](#replace) with an empty *Replace with*,
or [Trim around text](#trim-around-text).

## Change case

| Style | Example |
|---|---|
| **Title Case** | `the QUICK brown-fox` → `The Quick Brown-Fox` |
| **Sentence case** | `the QUICK brown fox` → `The quick brown fox` |
| **lowercase** | `Annual Report` → `annual report` |
| **UPPERCASE** | `readme` → `README` |
| **iNVERT** | `Hello` → `hELLO` |

Title Case starts a new word after a space, `_`, `-`, `.`, brackets, `&` or `+`. Set **Apply to → Extension** to change only the extension's case.

Case changes follow the same rules on every PC by default. Tick **Follow the Windows language's own
case rules** if you want your language's special cases, for example Turkish, where `I` becomes `ı`.

## Numbering

Adds a running number based on the **order of the list** (sort first if needed).

| Option | Notes |
|---|---|
| **Start at** / **Step** | e.g. start 10, step 10 → 10, 20, 30… Negative steps count down. |
| **Digits** | Zero-padding: 3 → `001`. 0 = no padding. |
| **Place the number** | **Before name**, **After name**, or **Replace name**. |
| **Separator** | Text between number and name, e.g. ` - ` or `_`. |
| **Restart numbering in each folder** | Each folder starts again at *Start at*. |

Excluded (unticked) files don't use up a number, and with **Only apply to some files** set, only the
files the rule applies to are counted. For a counter inside other text, use the `<n:3>`
token in [Insert](#insert) or [New name](#new-name).

## New name

Builds the whole name from a template.

| Option | Notes |
|---|---|
| **New name** (*tokens*) | e.g. `<exif:taken:yyyy-MM-dd_HHmmss>`. Use `<current>` for the name as the rules above left it, `<name>` for the original. |

| Template | Result |
|---|---|
| `<exif:taken:yyyy-MM-dd> <exif:camera>` | `2024-07-14 iPhone 15 Pro` |
| `<music:track:2> <music:title>` | `03 Yellow` |
| `<parent> <n:3>` | `Vacation 001` |
| `[<current>]` | `[whatever the rules above produced]` |

The extension is kept. Use the [Extension](#extension) rule to change it.

## Clean up

A bundle of tidy-ups applied in a fixed order. Ticked by default: underscores and `%20` → spaces, single spaces, trim.

| Group | Options |
|---|---|
| **Turn into spaces** | Underscores `_`, Dots `.`, Dashes `-`, `%20` (from web downloads) |
| **Remove** | Text in (round), [square] or {curly} brackets (with the brackets), Digits, Symbols (keeps letters, digits, spaces, `.`, `-`, `_`), Accents (`é` → `e`) |
| **Also remove these characters** | Any characters you list, e.g. `#!~` |
| **Spacing** | Single spaces only, Trim ends |
| **Finally, replace spaces with** | e.g. `_` or `-` |
| **Shorten to at most** | Maximum length in characters (0 = no limit). An emoji or accented letter is never cut in half. |

Example: `Movie.Name.2010.[1080p]_(x264)` with dots → spaces, square and round removed → `Movie Name 2010`.

## Extension

| Action | Example |
|---|---|
| **lowercase** (default) | `IMG.JPG` → `IMG.jpg` |
| **UPPERCASE** | `a.pdf` → `a.PDF` |
| **Change to** | `notes.txt` → `notes.md` |
| **Append** | `data.csv` → `data.csv.bak` |
| **Remove** | `script.sh` → `script` |

The new extension field accepts tokens. Folders are never given an extension.

## Pad numbers

| Option | Example |
|---|---|
| **Minimum digits** | 3: `Track 7 of 12` → `Track 007 of 012` |
| **Instead, remove leading zeros** | `Track 007` → `Track 7` |

Padding makes names sort correctly everywhere (`2` vs `10`). Every number in the name is padded,
including each side of a decimal point: `Version 5.1` → `Version 005.001`. Use *Only apply to some
files*, or a Replace rule with a regular expression, when only one number should change.

## Rearrange

Splits the name into parts and reassembles them.

| Option | Notes |
|---|---|
| **Split the name at** | The separator, e.g. ` - `. Leave empty to split at spaces. |
| **New order** (*tokens*) | `$1`, `$2`… are the parts; `$L` is the last part; `$-2` the second-to-last; `$0` the whole name. |

| Split at | New order | Before → After |
|---|---|---|
| ` - ` | `$2 - $1` | `Artist - Title` → `Title - Artist` |
| *(empty)* | `$L, $1` | `John Ronald Tolkien` → `Tolkien, John` |
| `_` | `$3_$2_$1` | `05_01_2024` → `2024_01_05` |

A name without the separator (only one part) is left unchanged. A part that doesn't exist, such as
`$3` in a two-part name, is empty. `$L` followed directly by a letter (`$Lx`) is just text.

## Trim around text

Removes everything before or after a marker, or around/between two markers.

| What to do | Without *Include the markers* | With *Include the markers* |
|---|---|---|
| **Remove after** `(` | `Song (Live)` → `Song (` | `Song (Live)` → `Song ` |
| **Remove before** `-` | `01 - Song` → `- Song` | `01 - Song` → ` Song` |
| **Keep between** `[` and `]` | `x [ABC] y` → `ABC` | `x [ABC] y` → `[ABC]` |
| **Remove between** `(` and `)` | `a (b) c` → `a () c` | `a (b) c` → `a  c` |

Other options: **Use the last match** (instead of the first) and **Match case**. Add a *Clean up*
rule afterwards to tidy leftover spaces.

## Move

Moves part of the name to somewhere else.

| Option | Notes |
|---|---|
| **Move** | **Characters** (by position) or **A piece of text** (exact text or a regex). |
| **How many / Starting at / Count from the end** | For characters. |
| **Text to move / Regular expression** | For text. The first match is moved. |
| **To** | **The start**, **The end**, or **A position**. |
| **Separator** | Placed between the moved piece and the rest (start/end only). The gap where the piece was is closed up; the rest of the name is not touched. |

| Settings | Before → After |
|---|---|
| 4 characters from the start → end | `2024 Report` → `Report 2024` |
| Text `\d{4}` (regex) → start | `Report 2024` → `2024 Report` |

## Reformat dates

Finds dates written one way and rewrites them another way.

| Option | Notes |
|---|---|
| **Dates written like** | One or more .NET date formats separated by `;`, e.g. `dd.MM.yyyy; MM-dd-yy; d MMM yyyy`. |
| **Rewrite as** | e.g. `yyyy-MM-dd`. |
| **Month names are in English** | For `MMM`/`MMMM` (Jan, February). Untick to use your Windows language. |

Format letters: `yyyy` year, `yy` 2-digit year, `MM` month, `MMM` Jan, `MMMM` January, `dd` day,
`d` day without zero, `HH` hour, `mm` minute, `ss` second.

| Written like | Rewrite as | Before → After |
|---|---|---|
| `dd.MM.yyyy` | `yyyy-MM-dd` | `Invoice 31.12.2024` → `Invoice 2024-12-31` |
| `d MMM yyyy` | `yyyy-MM-dd` | `Trip 5 Mar 2023` → `Trip 2023-03-05` |
| `MM-dd-yyyy` | `yyyyMMdd` | `12-31-2024 scan` → `20241231 scan` |

Things that look like dates but aren't real dates (`99.99.2024`) are left alone. A `/` or `:` in the
output becomes `-`, because Windows doesn't allow them in names.

## Transliterate

Converts other scripts and special letters to plain Latin.

| Convert | Example |
|---|---|
| **Any alphabet** (default) | `Café Ærøskøbing Łódź` → `Cafe Aeroskobing Lodz` |
| **Cyrillic** | `Москва` → `Moskva` |
| **Greek** | `Αθηνα` → `Athina` |
| **German umlauts** | `München Straße` → `Muenchen Strasse` |

**Your own replacements**: pairs like `&=and, @=at` (separate with commas, semicolons or new lines),
applied first. "Any alphabet" handles Cyrillic (Russian, Ukrainian, Belarusian, Serbian and
Macedonian letters), Greek, ligatures (`æ`, `ø`, `ł`…), accents and typographic quotes. Anything it
cannot convert, such as Chinese, Arabic, Hebrew or emoji, is **kept as it is**, and the rule shows a
warning naming those characters, so `写真.jpg` stays `写真.jpg` rather than becoming `.jpg`.

## Randomize

Adds random characters, for example to make names unique or anonymous.

| Option | Notes |
|---|---|
| **Length** | 1–64 characters. |
| **Characters** | `a-z 0-9`, `0-9`, `a-z`, `a-z A-Z`, `hex`. |
| **Place it** | Before name, After name, Replace name. |
| **Separator** | Between the random text and the name. |
| **Reshuffle** | Rolls new random values. |

The random text stays the same while you edit, so the preview is exactly what you get.

## List of names

Supplies names from a list. This is useful when the new names come from somewhere else.

**New names, in list order**: line 1 names the first ticked file in the list, line 2 the second,
and so on. Blank lines are ignored. Files beyond the end of the list keep their names.

```
Beach day <n:2>
Dinner
Sunset
```

**Old → new mapping**: each line is an old name, a separator, then the new name. The separator can
be a **tab**, `->`, `=>`, `|` or `,`. The old name can be the full path, the file name, or the name
without extension.

```
IMG_2041.JPG -> Lighthouse
IMG_2042.JPG -> Harbour at dusk
C:\Scans\scan001.pdf	Contract 2024
```

| Option | Notes |
|---|---|
| **Import .txt / .csv** | Loads the text from a file (e.g. a column pasted from Excel). |
| **Keep each file's extension** | On by default. If a new name already ends in the same extension, it isn't doubled. |

Tokens work in both modes. For renaming straight from a spreadsheet, also see [CSV import](09-csv.md).

## JavaScript

Write a few lines of JavaScript to do anything the other rules can't. This has its own page:
**[JavaScript scripting](06-javascript.md)**.

```js
// Turn "Show 1x02 Title" into "Show S01E02 Title"
name = name.replace(/(\d{1,2})x(\d{1,3})/i, (m, s, e) => 'S' + pad(s, 2) + 'E' + pad(e, 2));
```

---

← [Working with rules](03-rules.md) · Next: [Tokens and metadata](05-tokens.md) →
