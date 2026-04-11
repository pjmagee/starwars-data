// Class-level parallelism: methods within a single class run sequentially,
// classes run in parallel. Integration and Agent test classes mark themselves
// with [DoNotParallelize] when they share fixture state or external services.
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]
