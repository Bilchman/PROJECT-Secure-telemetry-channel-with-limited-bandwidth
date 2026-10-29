namespace Core;

public readonly record struct FrameCandidate(
    int FrameIndex,
    int WeightBits,
    int Value
);