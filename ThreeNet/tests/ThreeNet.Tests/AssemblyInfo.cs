using Xunit;

// GPU devices are expensive and some drivers dislike several of them being
// created at once, so the rendering tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
