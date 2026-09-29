/*
 * ii Reborn
 * Portions Copyright (C) 2025–2026 Goldentrophy Software
 * Licensed under GNU GPL v3.0-or-later — see LICENSE and NOTICE.
 * This file is part of a derivative work; see NOTICE for attribution
 * and modification history. Do not remove this notice.
 */

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace iiMenu.Managers
{
    public class FrameProfiler : MonoBehaviour
    {
        public static FrameProfiler instance;

        public static bool Enabled = true;
        public static float SpikeThresholdMs = 25f;
        public static float SpikeCooldownSeconds = 0.5f;
        public static float HeartbeatSeconds = 15f;

        private const int MaxSections = 16;
        private const int MaxDepth = 16;

        private static readonly string[] sectionNames = new string[MaxSections];
        private static readonly double[] sectionTotals = new double[MaxSections];
        private static readonly long[] sectionAlloc = new long[MaxSections];
        private static readonly int[] stackSlot = new int[MaxDepth];
        private static readonly long[] stackStart = new long[MaxDepth];
        private static readonly long[] stackHeap = new long[MaxDepth];
        private static int sectionCount;
        private static int stackDepth;

        private long lastTimestamp;
        private long lastHeap;
        private float lastSpikeTime;
        private float lastHeartbeatTime;
        private float worstFrameMs;
        private double frameTotalMs;
        private int frameTotalCount;
        private int slowFrames;
        private int verySlowFrames;
        private int heartbeatSpikes;

        private void Awake()
        {
            instance = this;
            lastTimestamp = Stopwatch.GetTimestamp();
            lastHeap = GC.GetTotalMemory(false);
            lastHeartbeatTime = Time.realtimeSinceStartup;

            try
            {
                if (File.Exists($"{PluginInfo.BaseDirectory}/iiMenu_DisableFrameLog.txt"))
                    Enabled = false;
            }
            catch { }

            bool incremental = false;
            string gcMode = "unknown";

            try
            {
                incremental = GarbageCollector.isIncremental;
                gcMode = GarbageCollector.GCMode.ToString();
            }
            catch { }

            LogManager.Log($"[Frame] profiler active={Enabled} threshold={SpikeThresholdMs:F0}ms incrementalGC={incremental} gcMode={gcMode}");

            StartCoroutine(FrameLoop());
        }

        public static void Begin(string name)
        {
            if (!Enabled || stackDepth >= MaxDepth)
                return;

            stackSlot[stackDepth] = FindSlot(name);
            stackStart[stackDepth] = Stopwatch.GetTimestamp();
            stackHeap[stackDepth] = GC.GetTotalMemory(false);
            stackDepth++;
        }

        public static void End()
        {
            if (stackDepth <= 0)
                return;

            stackDepth--;
            CloseSlot(stackSlot[stackDepth], stackStart[stackDepth], stackHeap[stackDepth], Stopwatch.GetTimestamp());
        }

        private static void CloseSlot(int slot, long start, long heapAtStart, long now)
        {
            if (start == 0)
                return;

            sectionTotals[slot] += (now - start) * 1000.0 / Stopwatch.Frequency;

            long allocated = GC.GetTotalMemory(false) - heapAtStart;

            if (allocated > 0)
                sectionAlloc[slot] += allocated;
        }

        private static int FindSlot(string name)
        {
            for (int i = 0; i < sectionCount; i++)
                if (sectionNames[i] == name)
                    return i;

            if (sectionCount >= MaxSections)
                return 0;

            sectionNames[sectionCount] = name;
            return sectionCount++;
        }

        private IEnumerator FrameLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                FrameTick();
            }
        }

        private void FrameTick()
        {
            long now = Stopwatch.GetTimestamp();
            double frameMs = (now - lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            lastTimestamp = now;

            if (!Enabled)
            {
                FlushSections();
                return;
            }

            long heap = GC.GetTotalMemory(false);
            long heapDelta = heap - lastHeap;
            lastHeap = heap;

            frameTotalMs += frameMs;
            frameTotalCount++;

            if ((float)frameMs >= SpikeThresholdMs)
            {
                slowFrames++;

                if (frameMs >= SpikeThresholdMs * 2f)
                    verySlowFrames++;
            }

            if ((float)frameMs > worstFrameMs)
                worstFrameMs = (float)frameMs;

            float t = Time.realtimeSinceStartup;

            if (frameMs >= SpikeThresholdMs && t - lastSpikeTime >= SpikeCooldownSeconds)
            {
                lastSpikeTime = t;
                heartbeatSpikes++;

                LogManager.Log($"[Frame] spike {frameMs:F1}ms | heap {heap / 1048576f:F1}MB ({heapDelta / 1024f:+0.0;-0.0;0.0}KB) | worst {worstFrameMs:F1}ms | {BuildSectionSummary()} | {SceneContext()}");

                lastTimestamp = Stopwatch.GetTimestamp();
            }

            if (t - lastHeartbeatTime >= HeartbeatSeconds)
            {
                lastHeartbeatTime = t;

                LogManager.Log($"[Frame] avg {frameTotalMs / Mathf.Max(1, frameTotalCount):F1}ms worst {worstFrameMs:F1}ms frames {frameTotalCount} slow {slowFrames} verySlow {verySlowFrames} spikes {heartbeatSpikes} | heap {heap / 1048576f:F1}MB | {BuildAllocSummary()} | {SceneContext()}");

                frameTotalMs = 0;
                frameTotalCount = 0;
                slowFrames = 0;
                verySlowFrames = 0;
                heartbeatSpikes = 0;
                worstFrameMs = 0f;
            }

            FlushSections();
        }

        private static void FlushSections()
        {
            long now = Stopwatch.GetTimestamp();

            for (int i = stackDepth - 1; i >= 0; i--)
                CloseSlot(stackSlot[i], stackStart[i], stackHeap[i], now);

            for (int i = 0; i < sectionCount; i++)
                sectionTotals[i] = 0;

            stackDepth = 0;
        }

        private static string BuildSectionSummary()
        {
            double t1 = 0, t2 = 0, t3 = 0;
            int i1 = -1, i2 = -1, i3 = -1;

            for (int i = 0; i < sectionCount; i++)
            {
                double value = sectionTotals[i];

                if (value < 0.25)
                    continue;

                if (value > t1)
                {
                    t3 = t2; i3 = i2;
                    t2 = t1; i2 = i1;
                    t1 = value; i1 = i;
                }
                else if (value > t2)
                {
                    t3 = t2; i3 = i2;
                    t2 = value; i2 = i;
                }
                else if (value > t3)
                {
                    t3 = value; i3 = i;
                }
            }

            if (i1 < 0)
                return "sections none";

            StringBuilder builder = new StringBuilder();
            builder.Append(sectionNames[i1]).Append(' ').Append(t1.ToString("F1")).Append("ms");

            if (i2 >= 0)
                builder.Append(", ").Append(sectionNames[i2]).Append(' ').Append(t2.ToString("F1")).Append("ms");

            if (i3 >= 0)
                builder.Append(", ").Append(sectionNames[i3]).Append(' ').Append(t3.ToString("F1")).Append("ms");

            return builder.ToString();
        }

        private static string BuildAllocSummary()
        {
            long a1 = 0, a2 = 0, a3 = 0;
            int i1 = -1, i2 = -1, i3 = -1;

            for (int i = 0; i < sectionCount; i++)
            {
                long value = sectionAlloc[i];

                if (value < 4096)
                    continue;

                if (value > a1)
                {
                    a3 = a2; i3 = i2;
                    a2 = a1; i2 = i1;
                    a1 = value; i1 = i;
                }
                else if (value > a2)
                {
                    a3 = a2; i3 = i2;
                    a2 = value; i2 = i;
                }
                else if (value > a3)
                {
                    a3 = value; i3 = i;
                }
            }

            for (int i = 0; i < sectionCount; i++)
                sectionAlloc[i] = 0;

            if (i1 < 0)
                return "alloc none";

            StringBuilder builder = new StringBuilder();
            builder.Append("alloc ").Append(sectionNames[i1]).Append(' ').Append(a1 / 1024).Append("KB");

            if (i2 >= 0)
                builder.Append(", ").Append(sectionNames[i2]).Append(' ').Append(a2 / 1024).Append("KB");

            if (i3 >= 0)
                builder.Append(", ").Append(sectionNames[i3]).Append(' ').Append(a3 / 1024).Append("KB");

            return builder.ToString();
        }

        private static string SceneContext()
        {
            string scene = SceneManager.GetActiveScene().name;
            string room = "single";
            int players = 0;

            try
            {
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                {
                    room = PhotonNetwork.CurrentRoom.Name;
                    players = PhotonNetwork.CurrentRoom.PlayerCount;
                }
            }
            catch { }

            return $"scene {scene} room {room} players {players}";
        }
    }
}
