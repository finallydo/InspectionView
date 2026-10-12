using System;
using OpenTK.Graphics.OpenGL4;

namespace InspectionView.App.Viewer
{
    /// <summary>GLSL 셰이더 프로그램 래퍼.</summary>
    public sealed class GlShader : IDisposable
    {
        public int Program { get; private set; }

        public GlShader(string vertexSource, string fragmentSource)
        {
            int vs = Compile(ShaderType.VertexShader, vertexSource);
            int fs = Compile(ShaderType.FragmentShader, fragmentSource);

            Program = GL.CreateProgram();
            GL.AttachShader(Program, vs);
            GL.AttachShader(Program, fs);
            GL.LinkProgram(Program);
            GL.GetProgram(Program, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0)
            {
                var log = GL.GetProgramInfoLog(Program);
                GL.DeleteProgram(Program);
                throw new InvalidOperationException("Shader link failed: " + log);
            }

            GL.DetachShader(Program, vs);
            GL.DetachShader(Program, fs);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }

        private static int Compile(ShaderType type, string source)
        {
            int id = GL.CreateShader(type);
            GL.ShaderSource(id, source);
            GL.CompileShader(id);
            GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                var log = GL.GetShaderInfoLog(id);
                GL.DeleteShader(id);
                throw new InvalidOperationException($"{type} compile failed: {log}");
            }
            return id;
        }

        public void Use() => GL.UseProgram(Program);

        public int Uniform(string name) => GL.GetUniformLocation(Program, name);

        public void Dispose()
        {
            if (Program != 0)
            {
                GL.DeleteProgram(Program);
                Program = 0;
            }
        }
    }

    /// <summary>뷰어에서 쓰는 셰이더 소스.</summary>
    internal static class ShaderSources
    {
        public const string ImageVertex = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
uniform mat4 uProj;
out vec2 vUv;
void main()
{
    gl_Position = uProj * vec4(aPos, 0.0, 1.0);
    vUv = aUv;
}";

        public const string ImageFragment = @"#version 330 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uTex;
uniform int uChannels;
uniform float uScale;
uniform float uOffset;
void main()
{
    vec4 t = texture(uTex, vUv);
    vec3 c = (uChannels == 1) ? vec3(t.r) : t.rgb;
    c = (c - vec3(uOffset)) * uScale;
    FragColor = vec4(c, 1.0);
}";

        public const string ColorVertex = @"#version 330 core
layout(location = 0) in vec2 aPos;
uniform mat4 uProj;
void main()
{
    gl_Position = uProj * vec4(aPos, 0.0, 1.0);
}";

        public const string ColorFragment = @"#version 330 core
out vec4 FragColor;
uniform vec4 uColor;
void main()
{
    FragColor = uColor;
}";
    }
}
