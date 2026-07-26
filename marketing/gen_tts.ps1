Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::Male)
$synth.SetOutputToWaveFile("C:\Users\Tony\.gemini\antigravity\scratch\marksmith\marketing\audio_cues\narration_voiceover_raw.wav")
$synth.Rate = 0
$synth.Volume = 100

$lines = @(
    "Let’s see why Marksmith matters, starting with AI markdown that looks recognizable before it becomes a polished document.",
    "AI assistants leave fingerprints, like heavy bolding, odd math delimiters, citation pips, and copy code artifacts.",
    "Marksmith cleans those artifacts and reshapes the content into formatting that matches your style.",
    "The WinUI 3 app keeps the workflow simple, moving from Source to Style to Preview and Export.",
    "Use the Paste tab as a live Markdown editor, with every keystroke updating the finished preview.",
    "The visual toolbar helps format text, add tables, create headings, and insert advanced Marksmith elements fast.",
    "When the document looks right, generate a PDF or export DOCX straight to your output folder.",
    "Marksmith can detect the AI source, normalize its quirks, and show exactly how many fixes were applied.",
    "Cleanup controls handle the obvious machine generated tells, including emoji, dashes, and AI specific artifacts.",
    "Personalize the structure too, shifting heading levels or changing bold and italic behavior across the whole file.",
    "Themes and layout controls apply live, so the preview, PDF, and DOCX all stay consistent.",
    "Mermaid diagrams render inline, inherit the active theme, and stay clean across built in styles.",
    "For Word exports, diagrams become native editable shapes instead of flat screenshots.",
    "That means teams can move boxes, edit labels, recolor lines, and refine diagrams directly in Word.",
    "Math is handled properly too, with clean rendering for formulas and technical documents.",
    "Code blocks get syntax highlighting, while the cleanup engine leaves code content safely untouched.",
    "Automation can watch the clipboard or a folder, then convert new Markdown with almost no clicks.",
    "The local REST API lets scripts send Markdown to Marksmith and receive PDF, DOCX, PPTX, or EPUB output.",
    "The browser connector can send replies from ChatGPT, Gemini, and Claude directly into Marksmith.",
    "For organizations, governance features can monitor AI usage and flag risky data without exposing clean messages.",
    "Advanced settings keep everyday controls simple, while revealing cleanup and formatting power tools when needed.",
    "The takeaway is simple, Marksmith turns AI style into your style, then exports professional documents ready to send."
)

foreach ($line in $lines) {
    $synth.Speak($line)
}

$synth.Dispose()
Write-Host "TTS raw narration generated successfully!"
