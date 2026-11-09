# 5. Tokens and metadata

[Knowledge base](README.md) › Tokens and metadata

A **token** is a placeholder in angle brackets that Proteus fills in for each file, such as `<name>`,
`<n:3>` or `<exif:taken:yyyy-MM-dd>`.

## Where tokens work

Tokens work in: Insert *text*, Replace *replace with*, New name, Rearrange *new order*, List of
names, the Extension rule's new extension, the **subfolder pattern** for copy/move, and JavaScript
through `token('...')`.

The easy way to use them: click a text box, open **Insert a token** in the rule editor, and click a chip.

## Syntax

```
<key>                  e.g. <name>
<key:argument>         e.g. <n:3>   <modified:yyyy-MM-dd>
<group:field:format>   e.g. <exif:taken:yyyyMMdd>   <music:track:2>
```

- Arguments after a number field (`:2`, `:3`) are **zero-padding** widths.
- Arguments after a date field are **.NET date formats** (see [Date formats](#date-formats)).
- Metadata that doesn't exist for a file becomes **empty text**. Proteus never fails because of missing metadata.
- Characters Windows forbids (`\ / : * ? " < > |`) coming from metadata are replaced with safe ones.
- An unknown token is left as typed, so a typo stands out in the preview.
- To write a literal `<` or `>`, use `\<` and `\>`.

## File

| Token | Gives | Example |
|---|---|---|
| `<name>` | Original name without extension | `IMG_2041` |
| `<ext>` | Original extension without the dot | `jpg` |
| `<fullname>` | Original name with extension | `IMG_2041.jpg` |
| `<parent>` | Containing folder's name | `Vacation` |
| `<parent:2>` | Folder two levels up | `Photos` |
| `<drive>` | Drive letter | `D` |
| `<size>` | Size, human readable | `2.4 MB` |
| `<size:b>` `<size:kb>` `<size:mb>` `<size:gb>` | Size in a fixed unit | `2516` |
| `<type>` | Windows file type | `PDF Document` |
| `<owner>` | Owner account | `steven` |
| `<firstline>` | First non-empty line of a text file (up to 120 characters; `#` heading marks removed) | `Shopping list` |
| `<hash:md5>` `<hash:sha1>` `<hash:sha256>` `<crc32>` | Content hash (lowercase hex) | `9e107d9d…` |
| `<hash:md5:8>` | First 8 characters of the hash | `9e107d9d` |

## Counters

| Token | Gives |
|---|---|
| `<n>` | Position among ticked files: 1, 2, 3… |
| `<n:3>` | Same, padded: 001, 002… |
| `<fn:2>` | Position within its own folder, padded |
| `<count>` | Number of files in the batch |

## Dates

| Token | Gives |
|---|---|
| `<modified:yyyy-MM-dd>` | Last modified |
| `<created:yyyy-MM-dd>` | Created |
| `<accessed:yyyy-MM-dd>` | Last opened |
| `<now:yyyy-MM-dd>` / `<now:HHmmss>` | Right now |

### Date formats

| Pattern | Gives | Pattern | Gives |
|---|---|---|---|
| `yyyy` | 2024 | `HH` | 14 (24-hour) |
| `yy` | 24 | `hh` | 02 (12-hour) |
| `MM` | 07 | `mm` | 05 (minutes) |
| `MMM` | Jul | `ss` | 09 |
| `MMMM` | July | `tt` | PM |
| `dd` | 04 | `ddd` | Thu |

Anything else is copied as typed: `<modified:yyyy-MM-dd_HHmm>` → `2024-07-04_1405`. A `/` or `:`
produced by a date becomes `-` or `.`, because Windows doesn't allow them in names.

## Photo (EXIF)

For JPEG, HEIC, PNG, TIFF, WebP and camera RAW files (CR2, CR3, NEF, ARW, DNG, ORF, RW2, RAF…).

| Token | Gives |
|---|---|
| `<exif:taken:yyyy-MM-dd_HHmmss>` | Date taken (falls back to *modified* when missing) |
| `<exif:camera>` / `<exif:make>` | Camera model / maker |
| `<exif:lens>` | Lens model |
| `<exif:iso>` `<exif:fnumber>` `<exif:exposure>` `<exif:focal>` | ISO, aperture, shutter speed, focal length |
| `<exif:width>` `<exif:height>` | Pixel size |

## Video

For MP4, MOV, MKV, WebM, AVI, WMV and anything else Windows can read.

| Token | Gives |
|---|---|
| `<video:duration>` | Length: `01-02-33`, or `02-33` under an hour |
| `<video:duration:h\hmm\m>` | Your own pattern: `1h02m` (see [Durations](#durations)) |
| `<video:resolution>` | `1920x1080` |
| `<video:quality>` | `8K`, `4K`, `1440p`, `1080p`, `720p`, `576p`, `480p` or `SD` |
| `<video:width>` `<video:height>` | Frame size |
| `<video:fps>` | Frame rate, e.g. `29.97` |
| `<video:bitrate>` | Total bitrate in kbps |
| `<video:date:yyyy-MM-dd>` | Date recorded (falls back to created) |
| `<video:title>` `<video:year>` | Embedded title and year |

## Music and audio

For MP3, FLAC, M4A/AAC, WMA, OGG, WAV and more.

| Token | Gives |
|---|---|
| `<music:artist>` `<music:albumartist>` `<music:album>` `<music:title>` | Tags |
| `<music:track:2>` `<music:disc>` | Track (padded) and disc number |
| `<music:year>` `<music:genre>` `<music:composer>` | More tags |
| `<audio:duration>` | Length, e.g. `03-45` |
| `<audio:bitrate>` `<audio:samplerate>` `<audio:channels>` | Technical details |
| `<id3:artist>` etc. | MP3-only ID3 reader (kept for compatibility) |

## Documents

Read straight from the file for **Word, Excel, PowerPoint (docx/xlsx/pptx and macro versions),
OpenDocument (odt/ods/odp) and PDF**, with no Office or Acrobat needed. Older formats such as `.doc`
use Windows' own reader.

| Token | Gives |
|---|---|
| `<doc:title>` `<doc:subject>` | Title, subject |
| `<doc:author>` `<doc:lastauthor>` | Author, last saved by |
| `<doc:company>` `<doc:keywords>` `<doc:category>` | Company, keywords, category |
| `<doc:pages>` `<doc:words>` `<doc:slides>` | Counts (`<doc:pages:3>` pads) |
| `<doc:revision>` `<doc:app>` | Revision number, program that made it |
| `<doc:created:yyyy-MM-dd>` `<doc:modified:yyyy-MM-dd>` | Document dates (from inside the file) |

## Any Windows property

`<prop:CanonicalName>` reads any property from the Windows Property System, the same data
Explorer shows on a file's **Details** tab. Dates take a format, and numbers take a padding width.

```
<prop:System.Comment>
<prop:System.Photo.DateTaken:yyyy-MM-dd>
<prop:System.Media.EpisodeNumber:2>
<prop:System.Rating>
```

Microsoft's list of property names is under "Windows Properties" in the Windows developer documentation.

## Durations

The default is `hh-mm-ss`, or `mm-ss` under an hour. To write your own pattern use `h`/`hh`
(total hours), `m`/`mm`, `s`/`ss`; put `\` before letters you want as text:

| Pattern | Result for 1 h 5 m 9 s |
|---|---|
| *(default)* | `01-05-09` |
| `h\hmm\m` | `1h05m` |
| `hh.mm.ss` | `01.05.09` |

---

← [Rule reference](04-rule-reference.md) · Next: [JavaScript scripting](06-javascript.md) →
