using System;
namespace LandingStats.App.RegressionTests;
internal sealed class RegressionTestSkipped : Exception
{
    public RegressionTestSkipped(string reason) : base(reason) { }
}
