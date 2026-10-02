// FullUI's recent-problems list is process-wide (like a log), so tests that look at it must not interleave with others.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
