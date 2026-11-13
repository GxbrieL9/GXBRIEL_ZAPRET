using Xunit;

// Disable parallel test execution because several tests override environment variables
// (GXBRIEL_ZAPRET_APPDATA_ROOT, GXBRIEL_ZAPRET_PROGRAMDATA_ROOT) and interact with shared filesystem caches.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
