# 13. Recipes

[Knowledge base](README.md) › Recipes

Step-by-step solutions to common jobs. Each one starts from an empty rule stack (the bin next to
**+ Add rule** → *Clear all*).

---

## Photos: date-and-time names

`IMG_2041.JPG` → `2024-07-14_153012.jpg`

1. Add the photos.
2. **+ Add rule → New name**: `<exif:taken:yyyy-MM-dd_HHmmss>`
3. **+ Add rule → Extension**: lowercase.
4. Mode menu → *When a name is already taken* → **Add (2), (3)…** (burst shots share a second).
5. **Rename**.

*Variation:* add the camera: `<exif:taken:yyyy-MM-dd_HHmmss> <exif:camera>`.

## Photos: sort into Year\Month folders

1. Rules as above (optional).
2. Mode → **Move**, destination `D:\Pictures`, subfolders `<exif:taken:yyyy>\<exif:taken:MM>`.
3. **Move N items**.

## Photos: fix the camera's clock

The camera was set an hour behind:

1. **Dates & attributes** → Created → *Photo date taken (EXIF)*, shift `60`.
2. Modified → *Photo date taken (EXIF)*, shift `60`.
3. **Update N items**.

## Music: `Track - Artist - Title` from tags

1. Add the album folder.
2. **New name**: `<music:track:2> - <music:artist> - <music:title>`
3. **Clean up** (defaults) to tidy any spaces.
4. **Rename**.

*Sort a music library:* Mode → **Move**, subfolders `<music:albumartist>\<music:year> - <music:album>`.

## Music: fix track numbers that sort wrongly

`Track 1.mp3 … Track 10.mp3` sort as 1, 10, 2…

**Pad numbers** → minimum digits `2`: `Track 01.mp3 … Track 10.mp3`.

## TV episodes

`Show.Name.s01e02.1080p.WEB.mkv` → `Show Name - S01E02.mkv`

1. **Clean up**: tick *Dots .* under *Turn into spaces*.
2. **Replace** (Regex): Find `^(.*?) s(\d+)e(\d+).*$`, Replace with `$1 - S$2E$3`.
3. **Change case**: Title Case. Then add **Replace** (Regex, *Match case*): Find `S(\d+)e(\d+)`,
   Replace with `S$1E$2`, to put the `E` back in capitals.

Or use one **JavaScript** rule:

```js
const m = name.match(/^(.*?)[ ._-]+s(\d{1,2})e(\d{1,3})/i);
if (m) {
  const show = m[1].replace(/[._]/g, ' ').trim();
  name = `${show} - S${pad(m[2], 2)}E${pad(m[3], 2)}`;
}
```

## TV episodes: copy the names from your media library

You have better copies of episodes and want them to take the **exact** names already used in your
Jellyfin, Plex or Kodi library, so you can copy them over the old files without breaking anything.

`Silicon.Valley.S01E03.2160p.WEB-DL.x265.mkv` → `Silicon Valley - 01x03 - Articles of Incorporation.mkv`

1. Clear the rule stack and add the new episode files.
2. `...` → **Match names from a folder…** → **Browse…** to the show's folder in the library
   (for example `Z:\media\tvshows\Silicon Valley (2014)`). Leave *Include subfolders* on for
   `Season 01`, `Season 02` folders.
3. Episodes are paired by season and episode number. Check the pairs, fix any with the drop-down,
   and press **Use … names**.
4. Check the preview, then **Rename**.
5. Copy the renamed files into the library folders, replacing the old ones.

See [Match names from a folder](09-csv.md#match-names-from-a-folder) for the details.

## Videos: add resolution and length

`clip.mp4` → `clip [1080p 03-42].mp4`

**Insert** at end: ` [<video:quality> <video:duration>]`

## Documents: name by title and author

**New name**: `<doc:author> - <doc:title>`. Files with no title in their properties keep an empty
title. To fall back to the original name, use a script:

```js
const title = token('doc:title') || original.name;
const author = token('doc:author');
name = author ? `${author} - ${title}` : title;
```

## Invoices: European dates to ISO

`Invoice 31.12.2024 Acme.pdf` → `Invoice 2024-12-31 Acme.pdf`

**Reformat dates**: written like `dd.MM.yyyy`, rewrite as `yyyy-MM-dd`.

Then **Move** the date to the front: **Move → A piece of text**, `\d{4}-\d{2}-\d{2}` (regex), to *The start*.

## Downloads: clean up web names

`My%20Report_FINAL(1).pdf` → `My Report Final.pdf`

1. **Clean up**: `%20` and underscores to spaces (default), tick *(round)* under *Remove*.
2. **Replace**: Find `final`, Replace with `Final` (or use **Change case → Title Case**).

## Remove a prefix from many files

`DSC_0001.jpg` → `0001.jpg`

**Trim around text**: *Remove before* `_`, tick *Include the markers*.
Or **Remove characters**: 4 from the start.

## Rename from an Excel list

1. In Excel: column A = current file name, column B = new name. Save as CSV next to the files.
2. `...` → **Import a rename list (CSV)…**
3. Check the preview, then **Rename**.

See [Names from a folder or a CSV](09-csv.md).

## Number files in a chosen order

1. Add files, click a column header to sort (or sort by **New name** after other rules).
2. **Numbering**: start 1, digits 3, separator ` - `, *Before name*.

## Make every name unique and anonymous

**Randomize**: length 12, `a-z 0-9`, *Replace name*. Press **Reshuffle** until you like it.

## Transliterate a Russian music collection

**Transliterate**: *Cyrillic*. Then **Clean up** with defaults.

## Make files read-only after organising

**Dates & attributes** → Read-only → *Turn on*. Can be combined with any rename in the same batch.

---

← [Settings and portable mode](12-settings-portable.md) · Next: [FAQ and troubleshooting](14-faq.md) →
