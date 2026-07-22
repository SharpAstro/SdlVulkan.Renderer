using System;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// One shared offscreen Vulkan context (VkInstance + device + offscreen target) for the whole
/// GPU render-test collection.
///
/// Repeated vkCreateInstance / device create-destroy segfaults Mesa lavapipe nondeterministically
/// -- the test process passes every assertion and then crashes at teardown with exit code 139.
/// cd77c11 fixed the per-test flavour (one shaped-text test was spinning up four instances); this
/// fixture fixes the whole-run flavour. Two things made it worse than cd77c11 addressed: every test
/// class stood up its own instance+device, AND xUnit ran the classes in PARALLEL (no collection was
/// defined, so each class was its own collection), so several instance/device lifecycles churned
/// concurrently across threads -- the worst case for the driver bug.
///
/// Creating the instance+device ONCE for the entire run and disposing them ONCE collapses those many
/// (and concurrent) create-destroy cycles to exactly one. Each test resizes the shared offscreen
/// target to its own dimensions via <see cref="VulkanContext.ResizeOffscreen"/> (the device, command
/// buffers, sync objects and vertex buffers stay put) and stands up its own <see cref="VkRenderer"/>.
/// Renderer/pipeline/atlas churn is cheap device-child allocation -- it never touches the instance or
/// device, so it is safe to repeat, and it is the same create-dispose path the multi-window host uses
/// when a window opens and closes.
///
/// <see cref="Context"/> is null when the host has no Vulkan ICD (a CI runner without
/// mesa-vulkan-drivers, macOS without MoltenVK): tests skip, exactly as the old per-test
/// TryCreateOffscreenContext helpers did.
/// </summary>
public sealed unsafe class OffscreenGpuFixture : IDisposable
{
    // Any size works -- every test ResizeOffscreen's to its own dimensions before rendering.
    private const uint InitialWidth = 128;
    private const uint InitialHeight = 128;

    /// <summary>The shared offscreen context, or null when no Vulkan ICD is available on the host.</summary>
    public VulkanContext? Context { get; }

    public OffscreenGpuFixture()
    {
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            // CreateOffscreen's device is created with ownsInstance: true, so disposing the context
            // tears down the device AND the instance -- one instance/device lifecycle for the run.
            Context = VulkanContext.CreateOffscreen(instance, InitialWidth, InitialHeight);
        }
        catch (Exception)
        {
            Context = null;
        }
    }

    public void Dispose() => Context?.Dispose();
}

/// <summary>
/// Groups every offscreen GPU render test into a single xUnit collection so they (a) share the one
/// <see cref="OffscreenGpuFixture"/> and (b) run serially -- a single VulkanContext must never be
/// driven from two threads at once, and serial execution is also what keeps the instance/device
/// lifecycle down to one (see the fixture summary).
/// </summary>
[CollectionDefinition("OffscreenGpu")]
public sealed class OffscreenGpuCollection : ICollectionFixture<OffscreenGpuFixture>
{
}
