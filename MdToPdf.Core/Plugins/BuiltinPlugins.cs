namespace MdToPdf.Plugins;

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
            "wrap": { "prefix": "@startuml\n", "suffix": "\n@enduml", "unlessContains": "@start" }
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
              "sha256": "c3ee71ff81ab97352082225574a140f20f5d6929d5f33d1097a1fe0e4161962a",
              "extract": true,
              "stripRoot": true
            },
            {
              "name": "graphviz.zip",
              "os": "mac",
              "arch": "x64",
              "source": "url",
              "url": "https://gitlab.com/api/v4/projects/4207231/packages/generic/graphviz-releases/15.1.0/Darwin_23.6.0_Graphviz-15.1.0-Darwin.zip",
              "sha256": "2f577e3ac08d391ce7a62a9977b1de737005f9010dbb6abd326e6a0bc1a7cb0c",
              "extract": true,
              "stripRoot": true
            }
          ],
          "render": {
            "command": "{dir}/bin/dot",
            "args": ["-Tsvg"],
            "input": "stdin",
            "output": "stdout",
            "timeoutSeconds": 20
          }
        }
        """,

        // Typst: single-binary modern typesetting — math/tables/figures without LaTeX. Windows
        // only until the installer learns .tar.xz (Linux/macOS release format).
        """
        {
          "manifestVersion": 1,
          "id": "typst",
          "name": "Typst Snippets",
          "description": "Renders ```typst code blocks — beautifully typeset math, tables, and figures without a LaTeX install. Downloads the official single-binary Typst release (~20 MB) on install. Windows only for now (Linux/macOS releases ship as .tar.xz, which the installer can't extract yet).",
          "version": "1.0.0",
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
    };
}
