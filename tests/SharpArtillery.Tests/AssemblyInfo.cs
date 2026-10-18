using Xunit;

// The run-loop tests measure wall-clock phases and keep cores busy; run them one at a time
// so a parallel test cannot skew their timing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
