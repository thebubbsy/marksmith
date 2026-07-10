namespace MdToPdf.Plugins;

// Curated, first-party plugin manifests shipped with the app (but whose payloads still download
// only on explicit install). These use the exact same plugin.json format as community plugins —
// the canonical, copy-me reference copies live in the marksmith-plugins repo.
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
    };
}
