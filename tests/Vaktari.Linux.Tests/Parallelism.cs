// **Two test classes here lend a static, and xUnit ran classes side by side.**
//
// SharedMimeInfo's description roots and LinuxLauncher's config home are both
// seams: a class points one at a temp directory, reads what it wrote, and puts
// the old value back. That is the right shape — an environment variable would
// be worse, being process-global and unrestorable in the same breath — but a
// borrow is only safe while nobody else is looking, and with classes running in
// parallel somebody else is.
//
// It has already cost this repository a red CI once: an override that also moved
// the GLOB database repointed it for the whole process, because that database is
// a Lazy loaded once, and every later class saw a machine with no mime types at
// all. Which classes those were varied by run, so the failure never named the
// same test twice.
//
// One collection for the assembly is the cheap and durable answer. The suite is
// a few hundred tests of pure logic and takes under a second either way, so
// there is nothing to trade off — and the Ui project has been serial for a
// related reason since its own headless flake.
[assembly: Xunit.CollectionBehavior(
    Xunit.CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]
