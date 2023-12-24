using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Jobs;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

namespace RuntimeVirtualTexture
{
    [Serializable]
    public class FeedbackReader
    {
        public Camera feedbackCamera;

        /*
         * For Reading Feedback buffer
         */
        private int m_FeedbackSize; // the size of the feedback buffer (scaled by feedbackFactor)
        private bool m_IsReadingBack;
        private bool m_HasData;
        private uint[] clearFeedbackData; // used for clearing feedback buffer per frame
        private ComputeBuffer m_FeedbackBuffer;
        private ComputeBuffer m_DebugBuffer;

        public RenderTexture m_FeedbackTex;
        public Camera mainCamera;

        /*
         * feedback Analysis
         */
        public List<uint> RequestList;
        private HashSet<uint> UniqueRequests;
        private static int m_JobNumber = 6;
        private NativeArray<uint> readbackDatas;
        private UnsafeHashSet<uint>[] requestLists;
        private JobHandle[] analysisJobHandles;

        public FeedbackReader(int feedbackHeight, int feedbackWidth, int feedbackFactor, int lodBias,
            Camera feedbackCamera, Camera mainCamera)
        {
            this.feedbackCamera = feedbackCamera;
            this.mainCamera = mainCamera;

            // For Reading Feedback buffer
            m_FeedbackSize = feedbackHeight * feedbackWidth;

            Shader.SetGlobalVector(Shader.PropertyToID("_FeedBackParam"),
                new Vector4(feedbackFactor, feedbackHeight, feedbackWidth, lodBias));

            /* Test */
            m_FeedbackTex = new RenderTexture(feedbackWidth, feedbackHeight, 0, GraphicsFormat.R8G8B8A8_UNorm)
            {
                useMipMap = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            m_FeedbackTex.Create();
            feedbackCamera.targetTexture = m_FeedbackTex;
            feedbackCamera.enabled = false;

            /* Test */

            clearFeedbackData = new uint[m_FeedbackSize];
            for (int i = 0; i < m_FeedbackSize; i++)
            {
                clearFeedbackData[i] = 0;
            }

            // used for feedback analysis
            m_IsReadingBack = false;
            m_HasData = false;

            analysisJobHandles = new JobHandle[m_JobNumber];

            /* helper var used for deduplication */
            UniqueRequests = new HashSet<uint>();
            /* actual request list used for rvt */
            RequestList = new List<uint>();
            // Debug.Log(new Vector4(feedbackFactor, feedbackHeight, feedbackWidth, 1));
        }

        public void FeedbackRender()
        {
            var fbTransform = feedbackCamera.transform;
            var mcTransform = mainCamera.transform;
            fbTransform.position = mcTransform.position;
            fbTransform.rotation = mcTransform.rotation;
            feedbackCamera.projectionMatrix = mainCamera.projectionMatrix;

            feedbackCamera.Render();
        }

        public void Initialize()
        {
            m_FeedbackBuffer = new ComputeBuffer(m_FeedbackSize, 4);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_FeedbackBuffer"), m_FeedbackBuffer);
            var cmd = new CommandBuffer();
            cmd.SetRandomWriteTarget(1, m_FeedbackBuffer, true);
            Graphics.ExecuteCommandBuffer(cmd);
#if UNITY_EDITOR
            m_DebugBuffer = new ComputeBuffer(m_FeedbackSize, 4);
#endif
            readbackDatas = new NativeArray<uint>(m_FeedbackSize, Allocator.Persistent);
            requestLists = new UnsafeHashSet<uint>[m_JobNumber];
            for (int i = 0; i < m_JobNumber; i++)
            {
                requestLists[i] = new UnsafeHashSet<uint>(1, Allocator.Persistent);
            }
        }

        public bool IsReading()
        {
            return m_IsReadingBack;
        }

        public bool HasData()
        {
            return m_HasData;
        }

        public void ReadFeedback(bool forceWait)
        {
            m_IsReadingBack = true;
            if (forceWait)
            {
                AsyncGPUReadback.Request(m_FeedbackBuffer, callback: ReadFeedbackCallBack).WaitForCompletion();
            }
            else
            {
                AsyncGPUReadback.Request(m_FeedbackBuffer, callback: ReadFeedbackCallBack);
            }
        }

        /*
        * Callback Function when finish reading the feedback buffer
        */
        void ReadFeedbackCallBack(AsyncGPUReadbackRequest request)
        {
            if (request.done && readbackDatas.IsCreated && !request.hasError)
            {
                m_HasData = true;
                request.GetData<uint>().CopyTo(readbackDatas);
            }
            else
            {
                m_HasData = false;
            }

            m_IsReadingBack = false;
        }

        public void Clear()
        {
            m_FeedbackBuffer.SetData(clearFeedbackData);
        }

        public void AnalysisDataNonParallel()
        {
            UniqueRequests.Clear();
            requestLists[0].Clear();

            foreach (var data in readbackDatas)
            {
                if (!UniqueRequests.Contains(data))
                {
                    UniqueRequests.Add(data);
                    RequestList.Add(data);
                }
            }

            // uint lastPixel = 0xffffffff;
            // foreach (var data in readbackDatas)
            // {
            //     if (data == lastPixel)
            //     {
            //         continue;
            //     }

            //     if (lastPixel != 0xffffffff)
            //     {
            //         if (!UniqueRequests.Contains(data))
            //         {
            //             UniqueRequests.Add(data);
            //             RequestList.Add(data);
            //         }
            //     }

            //     lastPixel = data;
            // }

            RequestList.Sort((x, y) => -x.CompareTo(y));
        }

        public void AnalysisData()
        {
            UniqueRequests.Clear();
            /*
             * split the readback data into N segments
             * so that we can use the Unity JobSystem to analysis it highly parallel
             */
            int segmentLength = Mathf.CeilToInt(readbackDatas.Length / (float)m_JobNumber);
            for (int i = 0; i < m_JobNumber; i++)
            {
                requestLists[i].Clear();
                FeedbackAnalysisTask analysisTask = new FeedbackAnalysisTask
                {
                    start = i * segmentLength,
                    end = (i + 1) * segmentLength < readbackDatas.Length
                        ? (i + 1) * segmentLength
                        : readbackDatas.Length,
                    data = readbackDatas,
                    requests = requestLists[i]
                };
                analysisJobHandles[i] = analysisTask.Schedule();
            }

            // Merge Request Lists
            Profiler.BeginSample("MergeRequests");

            for (int i = 0; i < m_JobNumber; i++)
            {
                analysisJobHandles[i].Complete();
                foreach (var req in requestLists[i])

                    if (!UniqueRequests.Contains(req))
                    {
                        UniqueRequests.Add(req);
                        RequestList.Add(req);
                    }
            }

            Profiler.EndSample();

            /*
             * sort by mipLevel
             * We firstly process the request which has bigger mipLevel
             */
            RequestList.Sort((x, y) => -x.CompareTo(y));
        }

        public void DrawDebugFeedback(FeedBackDebugger debugger)
        {
            // var data = new uint[m_FeedbackSize];
            // m_FeedbackBuffer.GetData(data, 0, 0, m_FeedbackSize);
            m_DebugBuffer.SetData(readbackDatas);
            debugger.DrawFeedBack(m_DebugBuffer);
        }

        public void Dispose()
        {
            m_FeedbackBuffer.Release();
            readbackDatas.Dispose();
#if UNITY_EDITOR
            m_DebugBuffer.Release();
#endif
            for (int i = 0; i < m_JobNumber; i++)
            {
                requestLists[i].Dispose();
            }
        }
    }
}