## 2024-05-30 - [Precompiled Regex]
**Learning:** Initializing Regex objects inside loops or repeatedly called methods without `[GeneratedRegex]` or `RegexOptions.Compiled` causes significant overhead because the engine has to parse the regex pattern every time.
**Action:** Always use `[GeneratedRegex]` for static regexes in .NET 7+, or `RegexOptions.Compiled` if generated regexes are not possible, to improve performance.

## 2024-05-30 - [String Concatenation in Loops]
**Learning:** Using `+` or string interpolation inside tight loops creates many intermediate string objects, leading to memory pressure and garbage collection pauses.
**Action:** Use `StringBuilder` for concatenating strings in loops, or `string.Create` / `Span<char>` for advanced scenarios where exact lengths are known.
