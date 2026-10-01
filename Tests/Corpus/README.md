# The corpus

Real XML documents, each copied byte for byte from the output of a text-conversion pipeline: ancient and classical works, richly tagged, in Greek, Hebrew, Syriac, English, and Sumerian literature in English translation. They are UTF-8 without a byte-order mark, with LF line ends. `SHA256SUMS` holds each file's hash, and `CorpusTests` refuses a file whose bytes have drifted from it. `.gitattributes` keeps git from converting their line ends, since every byte figure below depends on the bytes as they are.

| File | What it exercises |
|---|---|
| `homer.grk.pers/iliad.mq.xml` | 1.7 MB of Greek verse, about 18,000 elements: a line per element, milestones between them, speeches wrapped around lines |
| `herodotus.grk.pers/8.mq.xml` | Greek prose, and an empty element standing in for a chapter: `<chapter n="140" fill="base" />` |
| `aeschines.grk.pers/3.mq.xml` | Greek oratory, and one empty tag spelled tight inside a section: `<note/>` |
| `sumerian-lit.eng.etcsl/c222.mq.xml` | stanzas (`<lg>`) around numbered lines, rubrics inside a stanza but outside its lines |
| `sforno.tanach.eng.sef/lev.mq.xml` | commentary: several `<comment>` elements inside one verse, English with Hebrew quoted |
| `bible.eng.bsb.berean/ruth.mq.xml` | a translation whose paragraph marks are bare, tight empty tags: 71 `<p/>` |
| `philo.grk.f1gk/gig.mq.xml` | Greek with 278 bare `<lb/>` line-break marks |
| `tanach.uxlc/obad.mq.xml` | Hebrew with vowel points and accents: right-to-left text, many combining marks per letter |
| `pesh.ot.syrpat/obad.mq.xml` | Syriac: right-to-left text |

## Known figures

These were measured on these exact bytes by `grep -b`, independently of this library, and `CorpusTests` pins them.

| File | Element | Byte start | Length |
|---|---|---|---|
| `iliad.mq.xml` | the first `<milestone unit="card" n="1" />` | 346 | 31 |
| `iliad.mq.xml` | the first `<l n="1">` | 384 | 93 |
| `iliad.mq.xml` | the first `<l n="2">` | 484 | 90 |
| `iliad.mq.xml` | the first `<l n="611">` | 69,018 | 103 |
| `8.mq.xml` | `<chapter n="140" fill="base" />` | 202,083 | 31 |
| `3.mq.xml` | the first `<v n="29">`, which holds the `<note/>` | 33,299 | 942 |
| `c222.mq.xml` | the first stanza, `<lg type="kirugu" n="A">` | 274 | 5,465 |
