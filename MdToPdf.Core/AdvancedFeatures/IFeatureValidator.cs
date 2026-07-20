using System;

namespace MdToPdf.Core.AdvancedFeatures
{
    public interface IFeatureValidator
    {
        (bool IsValid, double Confidence, string[] Errors) Validate(string rawBlock);
    }

    public interface IFeatureDetector : IFeatureValidator
    {
        string FeatureName { get; }
        double Threshold { get; }
        
        bool Matches(string rawBlock);
    }
}
