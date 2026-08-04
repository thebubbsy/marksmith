namespace MarkSmith.Plugins;

// Curated, first-party plugin manifests shipped with the app (but whose payloads still download
// only on explicit install). These use the exact same plugin.json format as community plugins —
// the canonical, copy-me reference copies live in the marksmith-plugins repo, and every one of
// these has been verified live (real install, real render) before landing here.
internal static class BuiltinPlugins
{
    public static readonly string[] ManifestJson =
    {
        // PlantUML: there's no mature pure-JS PlantUML renderer the way Mermaid has
        // mermaid.min.js — full fidelity only exists in the actual Java engine — so this
        // downloads a private Temurin JRE + the MIT-licensed plantuml.jar. Smetana
        // (-Playout=smetana) is PlantUML's pure-Java layout engine, avoiding a native
        // Graphviz dependency.
        """
        {
          "manifestVersion": 1,
          "id": "plantuml",
          "name": "PlantUML Diagrams",
          "description": "Renders ```plantuml and ```puml code blocks as diagrams. Downloads a private Java runtime + the PlantUML engine (~90 MB) on install, isolated from any Java already on your system — nothing is bundled until you opt in.",
          "version": "1.0.0",
          "homepage": "https://plantuml.com",
          "license": "MIT (plantuml-mit)",
          "type": "diagram",
          "fenceLanguages": ["plantuml", "puml"],
          "runtime": { "kind": "jre", "majorVersion": 17 },
          "artifacts": [
            {
              "name": "plantuml.jar",
              "source": "github-latest",
              "repo": "plantuml/plantuml",
              "assetPattern": "^plantuml-mit-[\\d.]+\\.jar$"
            }
          ],
          "render": {
            "command": "{java}",
            "args": ["-Djava.awt.headless=true", "-jar", "{dir}/plantuml.jar", "-tsvg", "-pipe", "-charset", "UTF-8", "-Playout=smetana"],
            "input": "stdin",
            "output": "stdout",
            "timeoutSeconds": 20,
            "wrap": { "prefix": "@startuml\n", "suffix": "\n@enduml", "unlessContains": "@start" },
            "themeInject": {
              "mode": "afterStartTag",
              "text": "skinparam backgroundColor transparent\nskinparam defaultFontColor {themeText}\nskinparam ArrowColor {themeLine}\nskinparam ArrowFontColor {themeText}\nskinparam SequenceLifeLineBorderColor {themeLine}\nskinparam SequenceLifeLineBackgroundColor {themeBackground}\nskinparam SequenceGroupBackgroundColor {themeBackground}\nskinparam SequenceGroupBodyBackgroundColor transparent\nskinparam SequenceGroupBorderColor {themeLine}\nskinparam SequenceGroupFontColor {themeText}\nskinparam SequenceGroupHeaderFontColor {themeText}\nskinparam SequenceBoxBackgroundColor {themeBackground}\nskinparam SequenceBoxBorderColor {themeLine}\nskinparam SequenceBoxFontColor {themeText}\nskinparam SequenceDividerBackgroundColor {themeBackground}\nskinparam SequenceDividerBorderColor {themeLine}\nskinparam SequenceDividerFontColor {themeText}\nskinparam SequenceReferenceBackgroundColor {themeBackground}\nskinparam SequenceReferenceHeaderBackgroundColor {themeBackground}\nskinparam SequenceReferenceBorderColor {themeLine}\nskinparam SequenceReferenceFontColor {themeText}\nskinparam SequenceTitleFontColor {themeText}\nskinparam ParticipantBackgroundColor {themeBackground}\nskinparam ParticipantBorderColor {themeLine}\nskinparam ParticipantFontColor {themeText}\nskinparam ActorBackgroundColor {themeBackground}\nskinparam ActorBorderColor {themeLine}\nskinparam ActorFontColor {themeText}\nskinparam NoteBackgroundColor {themeBackground}\nskinparam NoteBorderColor {themeLine}\nskinparam NoteFontColor {themeText}\nskinparam ClassBackgroundColor {themeBackground}\nskinparam ClassBorderColor {themeLine}\nskinparam ClassFontColor {themeText}\nskinparam ClassAttributeFontColor {themeText}\nskinparam StateBackgroundColor {themeBackground}\nskinparam StateBorderColor {themeLine}\nskinparam StateFontColor {themeText}\nskinparam ActivityBackgroundColor {themeBackground}\nskinparam ActivityBorderColor {themeLine}\nskinparam ActivityFontColor {themeText}\nskinparam EntityBackgroundColor {themeBackground}\nskinparam EntityBorderColor {themeLine}\nskinparam EntityFontColor {themeText}\nskinparam DatabaseBackgroundColor {themeBackground}\nskinparam DatabaseBorderColor {themeLine}\nskinparam DatabaseFontColor {themeText}\nskinparam QueueBackgroundColor {themeBackground}\nskinparam QueueBorderColor {themeLine}\nskinparam QueueFontColor {themeText}\nskinparam ControlBackgroundColor {themeBackground}\nskinparam ControlBorderColor {themeLine}\nskinparam ControlFontColor {themeText}\nskinparam BoundaryBackgroundColor {themeBackground}\nskinparam BoundaryBorderColor {themeLine}\nskinparam BoundaryFontColor {themeText}\nskinparam CollectionsBackgroundColor {themeBackground}\nskinparam CollectionsBorderColor {themeLine}\nskinparam CollectionsFontColor {themeText}\nskinparam ComponentBackgroundColor {themeBackground}\nskinparam ComponentBorderColor {themeLine}\nskinparam ComponentFontColor {themeText}\nskinparam InterfaceBackgroundColor {themeBackground}\nskinparam InterfaceBorderColor {themeLine}\nskinparam InterfaceFontColor {themeText}\nskinparam PackageBackgroundColor {themeBackground}\nskinparam PackageBorderColor {themeLine}\nskinparam PackageFontColor {themeText}\nskinparam FrameBackgroundColor {themeBackground}\nskinparam FrameBorderColor {themeLine}\nskinparam FrameFontColor {themeText}\nskinparam RectangleBackgroundColor {themeBackground}\nskinparam RectangleBorderColor {themeLine}\nskinparam RectangleFontColor {themeText}\nskinparam CardBackgroundColor {themeBackground}\nskinparam CardBorderColor {themeLine}\nskinparam CardFontColor {themeText}\nskinparam StorageBackgroundColor {themeBackground}\nskinparam StorageBorderColor {themeLine}\nskinparam StorageFontColor {themeText}\nskinparam AgentBackgroundColor {themeBackground}\nskinparam AgentBorderColor {themeLine}\nskinparam AgentFontColor {themeText}\nskinparam CloudBackgroundColor {themeBackground}\nskinparam CloudBorderColor {themeLine}\nskinparam CloudFontColor {themeText}\nskinparam HexagonBackgroundColor {themeBackground}\nskinparam HexagonBorderColor {themeLine}\nskinparam HexagonFontColor {themeText}\nskinparam PersonBackgroundColor {themeBackground}\nskinparam PersonBorderColor {themeLine}\nskinparam PersonFontColor {themeText}\nskinparam StereotypeFontColor {themeText}\nskinparam TitleFontColor {themeText}\nskinparam HeaderFontColor {themeText}\nskinparam FooterFontColor {themeText}\nskinparam LegendFontColor {themeText}\nskinparam LegendBackgroundColor {themeBackground}\nskinparam LegendBorderColor {themeLine}"
            }
          }
        }
        """,

        // D2 (Terrastruct): modern architecture-diagram language, single Go binary per platform.
        """
        {
          "manifestVersion": 1,
          "id": "d2",
          "name": "D2 Diagrams",
          "description": "Renders ```d2 code blocks with Terrastruct's D2 — a modern diagram scripting language great for architecture and system diagrams. Downloads the official single-binary D2 release (~15 MB) on install.",
          "version": "1.0.0",
          "homepage": "https://d2lang.com",
          "license": "MPL-2.0",
          "type": "diagram",
          "fenceLanguages": ["d2"],
          "artifacts": [
            {
              "name": "d2.tar.gz",
              "os": "windows",
              "arch": "x64",
              "source": "github-latest",
              "repo": "terrastruct/d2",
              "assetPattern": "^d2-v[\\d.]+-windows-amd64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "d2.tar.gz",
              "os": "linux",
              "arch": "x64",
              "source": "github-latest",
              "repo": "terrastruct/d2",
              "assetPattern": "^d2-v[\\d.]+-linux-amd64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "d2.tar.gz",
              "os": "linux",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "terrastruct/d2",
              "assetPattern": "^d2-v[\\d.]+-linux-arm64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "d2.tar.gz",
              "os": "mac",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "terrastruct/d2",
              "assetPattern": "^d2-v[\\d.]+-macos-arm64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "d2.tar.gz",
              "os": "mac",
              "arch": "x64",
              "source": "github-latest",
              "repo": "terrastruct/d2",
              "assetPattern": "^d2-v[\\d.]+-macos-amd64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            }
          ],
          "render": {
            "command": "{dir}/bin/d2",
            "args": ["{input}", "{output}"],
            "input": "file",
            "inputExtension": ".d2",
            "output": "file",
            "timeoutSeconds": 30
          }
        }
        """,

        // Graphviz/DOT: the classic graph layout engine; checksum-pinned official 15.1.0 zips
        // (Graphviz publishes no portable Linux build, so Windows + macOS-Intel only).
        """
        {
          "manifestVersion": 1,
          "id": "graphviz",
          "name": "Graphviz / DOT",
          "description": "Renders ```dot / ```graphviz code blocks with the official Graphviz engine — the classic graph layout tool. Downloads the official Graphviz 15.1.0 release (~35 MB, checksum-pinned) on install. Windows and macOS (Intel) for now; Graphviz publishes no portable Linux build.",
          "version": "1.0.0",
          "homepage": "https://graphviz.org",
          "license": "EPL-1.0",
          "type": "diagram",
          "fenceLanguages": ["dot", "graphviz"],
          "artifacts": [
            {
              "name": "graphviz.zip",
              "os": "windows",
              "arch": "x64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/4207231/packages/generic/graphviz-releases/15.1.0/windows_10_cmake_Release_Graphviz-15.1.0-win64.zip",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "graphviz.zip",
              "os": "mac",
              "arch": "x64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/4207231/packages/generic/graphviz-releases/15.1.0/Darwin_23.6.0_Graphviz-15.1.0-Darwin.zip",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true,
              "stripRoot": true
            }
          ],
          "render": {
            "command": "{dir}/bin/dot",
            "args": ["-Tsvg", "-Gbgcolor=transparent", "-Gcolor={themeLine}", "-Gfontcolor={themeText}", "-Ncolor={themeLine}", "-Nfillcolor={themeBackground}", "-Nfontcolor={themeText}", "-Ecolor={themeLine}", "-Efontcolor={themeText}"],
            "input": "stdin",
            "output": "stdout",
            "timeoutSeconds": 20
          }
        }
        """,

        // Typst: single-binary modern typesetting — math/tables/figures without LaTeX. All
        // platforms now that the installer extracts .tar.xz (SharpCompress XZStream).
        """
        {
          "manifestVersion": 1,
          "id": "typst",
          "name": "Typst Snippets",
          "description": "Renders ```typst code blocks — beautifully typeset math, tables, and figures without a LaTeX install. Downloads the official single-binary Typst release (~20 MB) on install.",
          "version": "1.1.0",
          "homepage": "https://typst.app",
          "license": "Apache-2.0",
          "type": "diagram",
          "fenceLanguages": ["typst"],
          "artifacts": [
            {
              "name": "typst.zip",
              "os": "windows",
              "arch": "x64",
              "source": "github-latest",
              "repo": "typst/typst",
              "assetPattern": "^typst-x86_64-pc-windows-msvc\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "typst.zip",
              "os": "windows",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "typst/typst",
              "assetPattern": "^typst-aarch64-pc-windows-msvc\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "typst.tar.xz",
              "os": "linux",
              "arch": "x64",
              "source": "github-latest",
              "repo": "typst/typst",
              "assetPattern": "^typst-x86_64-unknown-linux-musl\\.tar\\.xz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "typst.tar.xz",
              "os": "linux",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "typst/typst",
              "assetPattern": "^typst-aarch64-unknown-linux-musl\\.tar\\.xz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "typst.tar.xz",
              "os": "mac",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "typst/typst",
              "assetPattern": "^typst-aarch64-apple-darwin\\.tar\\.xz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "typst.tar.xz",
              "os": "mac",
              "arch": "x64",
              "source": "github-latest",
              "repo": "typst/typst",
              "assetPattern": "^typst-x86_64-apple-darwin\\.tar\\.xz$",
              "extract": true,
              "stripRoot": true
            }
          ],
          "render": {
            "command": "{dir}/typst",
            "args": ["compile", "--format", "svg", "{input}", "{output}"],
            "input": "file",
            "inputExtension": ".typ",
            "output": "file",
            "timeoutSeconds": 30
          }
        }
        """,

        // Vega-Lite: JSON chart specs -> real data viz via the official vl-convert binary.
        """
        {
          "manifestVersion": 1,
          "id": "vega-lite",
          "name": "Vega-Lite Charts",
          "description": "Renders ```vega-lite / ```vegalite code blocks (JSON chart specs) as real data visualizations — bar charts, scatter plots, line charts and more. Downloads the official vl-convert single binary (~30 MB) on install.",
          "version": "1.0.0",
          "homepage": "https://vega.github.io/vega-lite/",
          "license": "BSD-3-Clause",
          "type": "diagram",
          "fenceLanguages": ["vega-lite", "vegalite"],
          "artifacts": [
            {
              "name": "vl-convert.zip",
              "os": "windows",
              "arch": "x64",
              "source": "github-latest",
              "repo": "vega/vl-convert",
              "assetPattern": "^vl-convert_win-64\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "vl-convert.zip",
              "os": "linux",
              "arch": "x64",
              "source": "github-latest",
              "repo": "vega/vl-convert",
              "assetPattern": "^vl-convert_linux-64\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "vl-convert.zip",
              "os": "linux",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "vega/vl-convert",
              "assetPattern": "^vl-convert_linux-aarch64\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "vl-convert.zip",
              "os": "mac",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "vega/vl-convert",
              "assetPattern": "^vl-convert_osx-arm64\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "vl-convert.zip",
              "os": "mac",
              "arch": "x64",
              "source": "github-latest",
              "repo": "vega/vl-convert",
              "assetPattern": "^vl-convert_osx-64\\.zip$",
              "extract": true,
              "stripRoot": true
            }
          ],
          "render": {
            "command": "{dir}/vl-convert",
            "args": ["vl2svg", "--input", "{input}", "--output", "{output}"],
            "input": "file",
            "inputExtension": ".vl.json",
            "output": "file",
            "timeoutSeconds": 30
          }
        }
        """,
        // LilyPond: engraved sheet music; 2.24.4 pinned (2.26.0 mingw build hard-crashes
        // with STATUS_STACK_BUFFER_OVERRUN on compile — empirically verified). Exit code is
        // nonzero even on success (SVG backend rejects the default pdf format) — harmless,
        // since the engine reads the output file, not the exit code.
        """
        {
          "manifestVersion": 1,
          "id": "lilypond",
          "name": "LilyPond Sheet Music",
          "description": "Renders ```lilypond code blocks as engraved sheet music with GNU LilyPond. Downloads the official LilyPond 2.24.4 release (~40 MB, checksum-pinned) on install.",
          "version": "1.0.0",
          "homepage": "https://lilypond.org",
          "license": "GPL-3.0",
          "type": "diagram",
          "fenceLanguages": ["lilypond", "ly"],
          "artifacts": [
            {
              "name": "lilypond.zip",
              "os": "windows",
              "arch": "x64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/lilypond%2Flilypond/packages/generic/lilypond/2.24.4/lilypond-2.24.4-mingw-x86_64.zip",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "lilypond.tar.gz",
              "os": "linux",
              "arch": "x64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/lilypond%2Flilypond/packages/generic/lilypond/2.24.4/lilypond-2.24.4-linux-x86_64.tar.gz",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "lilypond.tar.gz",
              "os": "mac",
              "arch": "aarch64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/lilypond%2Flilypond/packages/generic/lilypond/2.24.4/lilypond-2.24.4-darwin-arm64.tar.gz",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "lilypond.tar.gz",
              "os": "mac",
              "arch": "x64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/lilypond%2Flilypond/packages/generic/lilypond/2.24.4/lilypond-2.24.4-darwin-x86_64.tar.gz",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true,
              "stripRoot": true
            }
          ],
          "render": {
            "command": "{dir}/bin/lilypond",
            "args": ["-dbackend=svg", "-dno-point-and-click", "-o", "{outputBase}", "{input}"],
            "input": "file",
            "inputExtension": ".ly",
            "output": "file",
            "timeoutSeconds": 60,
            "wrap": { "prefix": "\\version \"2.24.4\"\n\\header { tagline = ##f }\n", "suffix": "", "unlessContains": "\\version" }
          }
        }
        """,

        // WaveDrom: digital timing diagrams. First plugin using the node runtime + npm
        // artifact source (upstream's "single file" release is not actually self-contained).
        """
        {
          "manifestVersion": 1,
          "id": "wavedrom",
          "name": "WaveDrom Timing Diagrams",
          "description": "Renders ```wavedrom code blocks (JSON signal descriptions) as digital timing diagrams. Downloads a private Node.js LTS runtime (~30 MB) plus wavedrom-cli from npm on install — isolated from any Node already on your system.",
          "version": "1.0.0",
          "homepage": "https://wavedrom.com",
          "license": "MIT",
          "type": "diagram",
          "fenceLanguages": ["wavedrom"],
          "runtime": { "kind": "node" },
          "artifacts": [
            {
              "name": "wavedrom-cli",
              "source": "npm",
              "package": "wavedrom-cli",
              "packageVersion": "3.2.0"
            }
          ],
          "render": {
            "command": "{node}",
            "args": ["{dir}/npm/node_modules/wavedrom-cli/wavedrom-cli.js", "-i", "{input}", "-s", "{output}"],
            "input": "file",
            "inputExtension": ".json5",
            "output": "file",
            "timeoutSeconds": 30
          }
        }
        """,
        // Pandoc importer: the first non-diagram plugin type — converts .rst/.org/.docx/… files
        // to Markdown on open/drop (see PluginFileReader). Pandoc infers the input format from
        // the file's own extension, so one args list covers every claimed extension. Archive
        // layouts differ per OS (pandoc.exe at the zip root on Windows, bin/pandoc in the
        // Linux/macOS tarballs) — hence the per-OS command overrides.
        """
        {
          "manifestVersion": 1,
          "id": "pandoc-import",
          "name": "Pandoc File Importer",
          "description": "Open or drop reStructuredText, Org-mode, MediaWiki, Textile, DOCX, ODT, RTF, and EPUB files — Pandoc converts them to Markdown on the way in. Downloads the official Pandoc release (~40 MB) on install.",
          "version": "1.0.0",
          "homepage": "https://pandoc.org",
          "license": "GPL-2.0-or-later",
          "type": "importer",
          "artifacts": [
            {
              "name": "pandoc.zip",
              "os": "windows",
              "arch": "x64",
              "source": "github-latest",
              "repo": "jgm/pandoc",
              "assetPattern": "^pandoc-[\\d.]+-windows-x86_64\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "pandoc.tar.gz",
              "os": "linux",
              "arch": "x64",
              "source": "github-latest",
              "repo": "jgm/pandoc",
              "assetPattern": "^pandoc-[\\d.]+-linux-amd64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "pandoc.tar.gz",
              "os": "linux",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "jgm/pandoc",
              "assetPattern": "^pandoc-[\\d.]+-linux-arm64\\.tar\\.gz$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "pandoc.zip",
              "os": "mac",
              "arch": "aarch64",
              "source": "github-latest",
              "repo": "jgm/pandoc",
              "assetPattern": "^pandoc-[\\d.]+-arm64-macOS\\.zip$",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "pandoc.zip",
              "os": "mac",
              "arch": "x64",
              "source": "github-latest",
              "repo": "jgm/pandoc",
              "assetPattern": "^pandoc-[\\d.]+-x86_64-macOS\\.zip$",
              "extract": true,
              "stripRoot": true
            }
          ],
          "import": {
            "extensions": ["rst", "org", "mediawiki", "wiki", "textile", "docx", "odt", "rtf", "epub"],
            "command": "{dir}/pandoc",
            "commandLinux": "{dir}/bin/pandoc",
            "commandMac": "{dir}/bin/pandoc",
            "args": ["{input}", "-t", "gfm", "--wrap=none", "--markdown-headings=atx"],
            "timeoutSeconds": 120
          }
        }
        """,

        // Office Capability: drives the INSTALLED Microsoft Word (via NetOffice, MIT) to produce
        // the 100%-accurate Word render for the Word-exact preview (tiled page-band rendering),
        // SmartArt/DrawingML fidelity, and docx verification. Harmless when Word is absent.
        // The payload is the office-host zip published from plugins/marksmith-office/dist in the
        // main repo — Install downloads + extracts it; Remove deletes the install dir and the
        // capability degrades gracefully.
        """
        {
          "manifestVersion": 1,
          "id": "marksmith-office",
          "name": "Office Capability (Word fidelity)",
          "description": "Drives the installed Microsoft Word via NetOffice to produce 100%-accurate renders of SmartArt and DrawingML shapes (Render Exactly as Word Would), powers the Word-exact tiled preview, and opens generated .docx in Word for verification. Requires Microsoft Office; harmless when absent.",
          "version": "1.0.0",
          "homepage": "https://github.com/NetOfficeFw/NetOffice",
          "license": "MIT (netoffice)",
          "type": "office",
          "artifacts": [
            {
              "name": "marksmith-office-host.zip",
              "url": "https://raw.githubusercontent.com/thebubbsy/marksmith/main/plugins/marksmith-office/dist/marksmith-office-host.zip",
              "sha256": "684a9f6be4757712a4e13dd91f89b72e34618d99784d9264b4bdd6802077c8e9",
              "extract": true
            }
          ]
        }
        """,
    };
}
