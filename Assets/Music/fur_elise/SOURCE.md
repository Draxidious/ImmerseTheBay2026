# Für Elise sheet images

`line01.jpg` to `line21.jpg` are the 21 lines of music of the full 5-page score
"Beethoven-ludwig-van-for-elise-549.pdf" on Wikimedia Commons, which Commons lists as **public domain**:
https://commons.wikimedia.org/wiki/File:Beethoven-ludwig-van-for-elise-549.pdf

Changes: each line is cropped from the 1920 px wide page render (1760 x 500 px, centred on the two staves).
The images are otherwise untouched: black notes on a white page.

| Lines | Page | Bars |
|---|---|---|
| 01 to 04 | 1 | 1 to 20 |
| 05 to 09 | 2 | 21 to 45 |
| 10 to 14 | 3 | 46 to 70 |
| 15 to 19 | 4 | 71 to 95 |
| 20 to 21 | 5 | 96 to 105 |

Bar numbers count the printed bars in order: the pickup shares bar 1's box, and the first and second endings are
bars 8 and 9. Bar positions for the highlight boxes are on the `SongNoteDisplay` component (`sheet` list) in
`JordanScene`. They were detected from the bar lines in the images: fraction = (x in page pixels - 80) / 1760.
