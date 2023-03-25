﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Python.Runtime;
using UnityEditor.Scripting.Python;
using UnityEngine;

namespace UnityEditor.GPT
{
    public class GPTWindow : EditorWindow
    {
        [MenuItem("AI/GPTWindow")]
        public static void Open()
        {
            var win = GetWindow<GPTWindow>();
            win.titleContent = new GUIContent(nameof(GPTWindow));
            win.minSize = new Vector2(400, 300);
            win.Show();
        }

        static GPTWindow s_Instance;

        Evaluator m_Evaluator;
        StringBuilder m_InputBuilder;
        StringBuilder m_CodeBuilder;
        Dictionary<AIType, string> m_ApiUrlKeys;

        GUIStyle m_OutputStyle;
        Vector2 m_OutputPos;
        string m_OutputText;
        Vector2 m_InputPos;
        string m_InputText;
        bool m_OnlyShowOutput;

        AIType m_CurrentType;
        Config m_CurrentData;
        int[] m_AITypeIDs;
        string[] m_AITypeNames;
        Dictionary<AIType, Config> m_Prompts;
        ProcessState m_ProcessState;
        SynchronizationContext unitySynchronizationContext;

        void OnEnable()
        {
            s_Instance = this;

            Evaluator.Init(ref m_Evaluator);
            Evaluator.Instance.OnEvaluationSuccess -= OnEvaluationSuccess;
            Evaluator.Instance.OnEvaluationSuccess += OnEvaluationSuccess;
            Evaluator.Instance.OnEvaluationError -= OnEvaluationError;
            Evaluator.Instance.OnEvaluationError += OnEvaluationError;

            FilePostprocessor.TextFileChanged -= TextFileChanged;
            FilePostprocessor.TextFileChanged += TextFileChanged;

            unitySynchronizationContext = SynchronizationContext.Current;
            m_ProcessState = new ProcessState();
            m_ProcessState.onReceivedMsg = new Action<string>(msg =>
            {
                unitySynchronizationContext.Post((d) =>
                {
                    AppendToOutput(msg);
                }, null);
            });

            m_InputBuilder = new StringBuilder();
            m_CodeBuilder = new StringBuilder();

            var aiTypes = Enum.GetValues(typeof(AIType));
            var len = aiTypes.Length;
            m_AITypeNames = new string[len];
            m_AITypeIDs = new int[len];

            for (int i = 0; i < len; i++)
            {
                var item = (AIType)aiTypes.GetValue(i);
                m_AITypeIDs[i] = (int)item;
                m_AITypeNames[i] = item.ToString();
            }

            InitPrompts();

            m_ApiUrlKeys = new Dictionary<AIType, string>()
            {
                { AIType.OpenAI, "API_URL" },
                { AIType.Bing, "BING_PROXY_URL" },
            };
        }

        void OnDisable()
        {
            if (Evaluator.Instance != null)
            {
                Evaluator.Instance.OnEvaluationSuccess -= OnEvaluationSuccess;
                Evaluator.Instance.OnEvaluationError -= OnEvaluationError;
            }

            FilePostprocessor.TextFileChanged -= TextFileChanged;

            m_ProcessState.Kill();
            m_ProcessState.Reset();
        }

        void OnGUI()
        {
            InitStyles();

            var windowSize = position.size;
            var execHeight = 50;
            var labelHeight = 16;

            if (!m_OnlyShowOutput)
            {
                windowSize.y -= execHeight;
                windowSize.y -= labelHeight * 2;
            }

            var outputWidth = windowSize.x;
            var outputHeight = windowSize.y * (m_OnlyShowOutput ? 1 : 0.5F);
            var inputHeight = windowSize.y * (m_OnlyShowOutput ? 0 : 0.5F);

            EditorGUILayout.BeginHorizontal(GUILayout.Height(labelHeight));
            {
                EditorGUILayout.LabelField(" Output", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                m_OnlyShowOutput = GUILayout.Toggle(m_OnlyShowOutput, "Only Show Output");
                if (GUILayout.Button("Clear Output"))
                    m_OutputText = string.Empty;
            }
            EditorGUILayout.EndHorizontal();

            if (m_ProcessState.scrollToBottom)
            {
                m_ProcessState.scrollToBottom = false;
                m_OutputPos.y = m_OutputStyle.CalcHeight(new GUIContent(m_OutputText), outputWidth);
                if (m_ProcessState.isFinished)
                {
                    TryExecCode(m_ProcessState.recvText.ToString());
                    m_ProcessState.Reset();
                }
            }

            m_OutputPos = EditorGUILayout.BeginScrollView(m_OutputPos, GUILayout.Height(outputHeight));
            {
                GUILayout.TextField(m_OutputText, m_OutputStyle, GUILayout.ExpandHeight(true));
            }
            EditorGUILayout.EndScrollView();

            if (inputHeight == 0)
                return;

            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField(" Input Command", EditorStyles.boldLabel, GUILayout.Height(labelHeight));
                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("AI:", EditorStyles.boldLabel, GUILayout.Width(20));
                GUI.changed = false;
                m_CurrentType = (AIType)EditorGUILayout.IntPopup((int)m_CurrentType, m_AITypeNames, m_AITypeIDs, GUILayout.Width(80));
                if (GUI.changed)
                    UpdateCurrentPromptData();

                GUILayout.Space(10);

                EditorGUILayout.LabelField("Prompt:", EditorStyles.boldLabel, GUILayout.Width(50));
                if (m_CurrentData != null)
                    m_CurrentData.Index = EditorGUILayout.Popup(m_CurrentData.Index, m_CurrentData.Names);
                else
                    EditorGUILayout.LabelField(string.Empty, EditorStyles.popup);
            }
            EditorGUILayout.EndHorizontal();
            m_InputPos = EditorGUILayout.BeginScrollView(m_InputPos, GUILayout.Height(inputHeight));
            {
                m_InputText = GUILayout.TextArea(m_InputText, GUILayout.ExpandHeight(true));
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginVertical(GUILayout.Height(execHeight));
            {
                EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(m_InputText) || m_ProcessState.isProcessing);
                if (GUILayout.Button("Execute", GUILayout.Height(32)))
                {
                    var inputText = m_InputText.Trim();
                    m_InputBuilder.Clear();

                    if (m_CurrentData != null && m_ApiUrlKeys.ContainsKey(m_CurrentType))
                    {
                        m_InputBuilder.AppendLine($"import os");
                        m_InputBuilder.AppendLine($"os.environ['{m_ApiUrlKeys[m_CurrentType]}']='{m_CurrentData.api_url ?? string.Empty}'");
                    }

                    m_InputBuilder.AppendLine($"import gpt");
                    m_InputBuilder.AppendLine($"gpt.set_prompt('''{m_CurrentData?.GetValue() ?? string.Empty}''')");
                    m_InputBuilder.AppendLine($"gpt.ask_{m_CurrentType.ToString().ToLower()}('''{inputText}''')");

                    AppendToOutput($"<color=#FFCC00>You</color>: {inputText}\n<color=#00CCFF>{m_CurrentType}</color>: ");
                    RunScript(m_InputBuilder.ToString());
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndVertical();
        }

        void InitStyles()
        {
            if (m_OutputStyle == null)
            {
                m_OutputStyle = new GUIStyle(EditorStyles.textField);
                m_OutputStyle.richText = true;
                m_OutputStyle.wordWrap = true;
            }
        }

        void InitPrompts()
        {
            if (m_Prompts == null)
                m_Prompts = new Dictionary<AIType, Config>();

            var folderPath = Path.Combine(Application.dataPath, "IntegrationGPT");
            var aiTypes = Enum.GetValues(typeof(AIType));
            for (int i = 0; i < aiTypes.Length; i++)
            {
                var type = (AIType)aiTypes.GetValue(i);
                var fileName = $"{type.ToString().ToLower()}_config.json";
                var filePath = Path.Combine(folderPath, fileName);

                if (!File.Exists(filePath))
                {
                    m_Prompts.Remove(type);
                    continue;
                }

                var fileContent = File.ReadAllText(filePath);
                try
                {
                    if (!m_Prompts.TryGetValue(type, out var promptData))
                    {
                        promptData = new Config();
                        m_Prompts.Add(type, promptData);
                    }

                    var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Config>(fileContent);
                    promptData.SetConfig(config);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            UpdateCurrentPromptData();
        }

        void AppendToOutput(string str)
        {
            m_OutputText += str;
            m_ProcessState.scrollToBottom = true;

            Repaint();
            GUI.FocusControl(string.Empty);
        }

        void UpdateCurrentPromptData()
        {
            m_Prompts?.TryGetValue(m_CurrentType, out m_CurrentData);
        }

        async void TryExecCode(string msg)
        {
            var match = Regex.Match(msg, @"```(?:csharp)?\s*(.*?)\s*```", RegexOptions.Singleline);
            if (match.Success)
            {
                var code = match.Groups[1].Value.Trim();
                if (code.StartsWith("using"))
                {
                    try
                    {
                        await Evaluator.Instance.Evaluate(code);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("代码编译失败, 请检查原代码块.(Code compilation error, please check the original code block.)");
                        Debug.LogException(ex);
                    }
                }
            }
        }

        void OnEvaluationError(string output, Microsoft.CodeAnalysis.Scripting.CompilationErrorException error)
        {
            Debug.LogError(output);
            Debug.LogException(error);
        }

        async void OnEvaluationSuccess(object output)
        {
            try
            {
                var error = await Evaluator.Instance.EvaluateSilently("var method = typeof(TemplateClass).GetMethod(\"Test\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); method?.Invoke(null, null);");
                if (error != null)
                    Debug.LogException(error);
            }
            catch (Exception ex)
            {
                Debug.LogError("代码执行失败, 请检查原代码块.(Code execution error, please check the original code block.)");
                Debug.LogException(ex);
            }
        }

        void TextFileChanged()
        {
            InitPrompts();
        }

        void RunScript(string code)
        {
            PythonRunner.EnsureInitialized();
            using (Py.GIL())
            {
                m_CodeBuilder.Clear();
                m_CodeBuilder.AppendLine("import sys");
                m_CodeBuilder.AppendLine("import os");

                var packagePaths = PythonSettings.GetSitePackages().ToList().ConvertAll(x => Path.Combine(Application.dataPath.Replace("Assets", string.Empty), x).Replace("\\", "/"));
                foreach (var item in packagePaths)
                    m_CodeBuilder.AppendLine($"sys.path.append('{item}')");

                var scriptsAssemblies = Path.GetFullPath("Library/ScriptAssemblies");
                scriptsAssemblies = scriptsAssemblies.Replace("\\", "/");
                m_CodeBuilder.AppendLine($"sys.path.append('{scriptsAssemblies}')");

                m_CodeBuilder.AppendLine($"os.environ['GPT_PATH']='{packagePaths[1]}'");
                m_CodeBuilder.AppendLine(code);

                var codeFileName = "Temp/template.py";
                File.WriteAllText(codeFileName, m_CodeBuilder.ToString(), Encoding.UTF8);

                var args = new List<string>
                {
                    $"\"{codeFileName}\""
                };

                m_ProcessState.Kill();
                m_ProcessState.SetStart();
                m_ProcessState.process = PythonRunner.SpawnPythonProcess(args, null, false, false, false, false);

                // debug code
                //var process = m_ProcessState.process;
                //process.WaitForExit();
                //var retcode = process.ExitCode;
                //var output = process.StandardOutput.ReadToEnd();
                //var errors = process.StandardError.ReadToEnd();

                //if (retcode != 0)
                //{
                //    Debug.LogError(errors);
                //    Debug.LogError(output);
                //}
            }
        }
    }

    public enum AIType
    {
        OpenAI = 0,
        Bing,
        Bard,
    }

    [Serializable]
    class Config
    {
        // json key
        public string api_url;
        public Dictionary<string, string> prompts;

        // internal field
        public int Index;
        public string[] Names;

        public void SetConfig(Config config)
        {
            // copy
            api_url = config.api_url;
            prompts = config.prompts;

            // init
            Names = prompts.Keys.ToArray();
            if (Index < 0 || Index >= Names.Length)
                Index = 0;
        }

        public string GetValue()
        {
            var index = Index;
            if (index >= 0 && index < Names.Length)
            {
                var name = Names[index];
                prompts.TryGetValue(name, out var value);
                return value;
            }

            return string.Empty;
        }
    }

    class ProcessState
    {
        public System.Diagnostics.Process process;
        public bool isProcessing;
        public bool isFinished;
        public StringBuilder recvText;
        public bool scrollToBottom;
        public Action<string> onReceivedMsg;

        Thread thread;
        Socket socket;

        public ProcessState()
        {
            recvText = new StringBuilder();
        }

        public void SetStart()
        {
            Reset();
            isProcessing = true;
            recvText.Clear();

            StartThread();
        }

        public void SetFinish()
        {
            isFinished = true;
        }

        public void Reset()
        {
            isProcessing = false;
            isFinished = false;

            StopThread();
        }

        public void Kill()
        {
            if (process != null && !process.HasExited)
                process.Kill();

            StopThread();
        }

        void StartThread()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            thread = new Thread(new ParameterizedThreadStart(StartListener));
            thread.Start(socket);
        }

        void StopThread()
        {
            try
            {
                socket.Close();
                socket = null;
            }
            catch { }

            try
            {
                thread?.Abort();
                thread = null;
            }
            catch { }
        }

        void StartListener(object obj)
        {
            var listener = obj as Socket;
            var localEndPoint = new IPEndPoint(IPAddress.Any, 10086);
            listener.Bind(localEndPoint);
            listener.Listen(1);

            var handler = listener.Accept();
            var buffer = new byte[1024];

            while (true)
            {
                var bytesReceived = handler.Receive(buffer);
                if (bytesReceived == 0)
                {
                    onReceivedMsg?.Invoke("\n\n");
                    SetFinish();
                    break;
                }
                else
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, bytesReceived);
                    recvText.Append(message);
                    onReceivedMsg?.Invoke(message);
                }
            }

            handler.Shutdown(SocketShutdown.Both);
            handler.Close();
        }
    }

    class FilePostprocessor : AssetPostprocessor
    {
        public static event Action TextFileChanged;

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var item in importedAssets)
            {
                var ext = Path.GetExtension(item);
                if (ext == ".json" || ext == ".txt")
                {
                    TextFileChanged?.Invoke();
                    break;
                }
            }
        }
    }
}