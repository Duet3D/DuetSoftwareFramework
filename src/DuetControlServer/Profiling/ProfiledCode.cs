using DuetControlServer.Profiling;

// The code a profiling build weaves Tracy zones into. Each entry covers the namespace or type named
// and everything below it, so this list is the one place that decides what shows up on the timeline.
//
// Every profiled method costs a field read and two calls into the Tracy client on each invocation,
// whether or not the GUI is connected, so widening this to the whole assembly slows down what is
// being measured. Narrow it to the subsystem under investigation instead: the sampling profile from
// dotnet-trace is the tool for finding out which subsystem that is.
[assembly: ProfileZone("DuetControlServer")]
