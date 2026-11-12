using Xunit;

namespace Planwharf.Native.Tests;

// Tests that start the real server run one at a time: a server that quits frees its port, and a
// server started meanwhile by another test could take it and answer in its place.
[CollectionDefinition("Real server", DisableParallelization = true)]
public sealed class RealServer;
