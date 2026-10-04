// The ASCOM simulators are single instances shared by the whole machine: tests that use them run one after the other, never together.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
