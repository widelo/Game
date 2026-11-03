using NUnit.Framework;

namespace CS462.StalkerAI.Tests
{
    public sealed class AStarTests
    {
        [Test] public void EmptyGridAndIdentity() => CoreVerification.EmptyGridAndIdentity();
        [Test] public void NoCornerCuttingAndInvalidEndpoints() => CoreVerification.NoCornerCuttingAndInvalidEndpoints();
        [Test] public void EdgesMatterAndSearchCanBeReused() => CoreVerification.EdgesMatterAndSearchCanBeReused();
        [Test] public void BudgetPauseAndDeterminism() => CoreVerification.BudgetPauseAndDeterminism();
        [Test] public void LiveEdgeFilterAndIndependentSearches() => CoreVerification.LiveEdgeFilterAndIndependentSearches();
        [Test] public void RandomizedOptimalityAgainstDijkstra() => CoreVerification.RandomizedOptimalityAgainstDijkstra();
    }
}
