namespace FalconLoad.Core.Model;

/// <summary>What the stages of a <see cref="LoadProfile"/> set over time.</summary>
public enum StageMode
{
    /// <summary>Each stage's target is the number of virtual users sending requests, each as fast as it can.</summary>
    VirtualUsers,

    /// <summary>Each stage's target is requests per second. <see cref="LoadProfile.Concurrency"/> is then the pool of virtual users that can send them.</summary>
    RequestRate,
}

/// <summary>One part of a staged load profile: for <see cref="DurationSeconds"/>, the load is held at or moves towards <see cref="Target"/>.</summary>
public sealed class LoadStage
{
    public int DurationSeconds { get; set; } = 60;

    /// <summary>Virtual users or requests per second, depending on <see cref="LoadProfile.StageMode"/>. 0 pauses the load.</summary>
    public double Target { get; set; }

    /// <summary>
    /// When true the load moves in a straight line from where the previous stage ended (0 for the first stage) to
    /// <see cref="Target"/>. When false it jumps to <see cref="Target"/> at once and stays there.
    /// </summary>
    public bool Ramp { get; set; }
}
