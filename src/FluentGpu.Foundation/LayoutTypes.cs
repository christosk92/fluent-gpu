namespace FluentGpu.Foundation;

/// <summary>Main-axis distribution of free space (flexbox justify-content).</summary>
public enum FlexJustify : byte { Start = 0, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }

/// <summary>Cross-axis alignment (flexbox align-items / align-self). <see cref="Auto"/> on a child = inherit the container's align-items.</summary>
public enum FlexAlign : byte { Auto = 0, Start, Center, End, Stretch }
