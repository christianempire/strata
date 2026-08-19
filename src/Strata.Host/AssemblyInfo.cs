using System.Runtime.CompilerServices;

// The scheduler's progress arithmetic and the API's parameter handling are internal and worth
// testing. Everything in this project is internal by convention, so the alternative is making a
// type public purely to assert on it.
[assembly: InternalsVisibleTo("Strata.Tests")]
