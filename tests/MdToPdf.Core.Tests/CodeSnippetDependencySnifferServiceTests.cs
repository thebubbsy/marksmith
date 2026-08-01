using System.Linq;
using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class CodeSnippetDependencySnifferServiceTests
    {
        [Fact]
        public void SniffDependencies_DetectsJsPythonAndCsDependencies()
        {
            var markdown = @"
# Code Example

```js
import express from 'express';
const fs = require('fs');
```

```python
import numpy as np
from sklearn.linear_model import LinearRegression
```

```cs
using Newtonsoft.Json;
using System.Text;
```
";
            var service = new CodeSnippetDependencySnifferService();
            var deps = service.SniffDependencies(markdown);

            Assert.NotNull(deps);
            Assert.Contains(deps, d => d.PackageName == "express" && d.Language == "js");
            Assert.Contains(deps, d => d.PackageName == "fs" && d.Language == "js");
            Assert.Contains(deps, d => d.PackageName == "numpy" && d.Language == "python");
            Assert.Contains(deps, d => d.PackageName == "sklearn.linear_model" && d.Language == "python");
            Assert.Contains(deps, d => d.PackageName == "Newtonsoft.Json" && d.Language == "cs");

            // System namespace should be excluded
            Assert.DoesNotContain(deps, d => d.PackageName == "System.Text");
        }

        [Fact]
        public void SniffDependencies_HandlesEmptyAndNoCodeFences()
        {
            var service = new CodeSnippetDependencySnifferService();
            var deps = service.SniffDependencies("Just plain text with no code blocks");

            Assert.Empty(deps);
        }
    }
}
