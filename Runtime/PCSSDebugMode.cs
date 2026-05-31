namespace PCSS.Runtime
{
    // Selects which intermediate buffer the debug pass blits to the screen.
    public enum PCSSDebugMode
    {
        None = 0,
        ReconMask = 1,          // quarter-res reconnaissance mask (_PCSS_ReconMask)
        ScreenSpaceShadow = 2,  // full-res result (_CustomScreenSpaceShadowmap)
    }
}
