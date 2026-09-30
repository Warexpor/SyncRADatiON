using Xunit;

// The shimmed ModRuntime.Log, NetWire.Warned and SessionReset registry are process-wide statics shared by several test classes.
// The whole suite runs in about a second, so serial execution costs nothing and removes cross-class interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
