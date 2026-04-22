using System.Runtime.CompilerServices;

// Exposes the Internal/ parsers to DistributedDebugger.Eval.Tests so they can
// be tested in isolation without having to be public.
[assembly: InternalsVisibleTo("DistributedDebugger.Eval.Tests")]
