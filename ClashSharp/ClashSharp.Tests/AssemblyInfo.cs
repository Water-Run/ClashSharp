/*
 * Test Assembly Configuration
 * Configures xUnit execution behavior for tests that share process-wide Windows settings services
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/AssemblyInfo.cs
 * @date: 2026-06-24
 */

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
