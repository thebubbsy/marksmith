using System.Runtime.CompilerServices;

// The accuracy test suite exercises a few internal helpers directly (version comparison, bounded
// body reads) that aren't part of the public app surface.
[assembly: InternalsVisibleTo("MdToPdf.Core.Tests")]
