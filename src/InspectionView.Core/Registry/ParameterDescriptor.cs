using System;
using System.Globalization;
using System.Reflection;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;
using InspectionView.Core.Types;

namespace InspectionView.Core.Registry
{
    public enum ParameterDirection
    {
        Input,
        Output,
    }

    /// <summary>입력/출력 파라미터 메타데이터. 에디터 자동완성, 파라미터 창, 노드 UI, Job 저장에 사용한다.</summary>
    public sealed class ParameterDescriptor
    {
        public string Name { get; }
        public string Description { get; }
        public ParameterDirection Direction { get; }
        public Type ValueType { get; }
        public object? DefaultValue { get; }
        public bool IsOptional { get; }
        public int Order { get; }
        public double Min { get; }
        public double Max { get; }
        public string[]? Choices { get; }
        public PropertyInfo Property { get; }

        /// <summary>아이코닉 객체(Image/Region/XLD/ObjectArray) 타입인지.</summary>
        public bool IsIconic => typeof(IconicObject).IsAssignableFrom(ValueType);

        /// <summary>스크립트 사용자에게 보여줄 타입 이름.</summary>
        public string TypeDisplayName => DisplayNameOf(ValueType);

        internal ParameterDescriptor(PropertyInfo property, InputAttribute attr, int declarationIndex)
        {
            Property = property;
            Direction = ParameterDirection.Input;
            Name = string.IsNullOrEmpty(attr.Name) ? property.Name : attr.Name!;
            Description = attr.Description;
            ValueType = property.PropertyType;
            DefaultValue = attr.Default;
            IsOptional = attr.Optional || attr.Default != null;
            Order = attr.Order == int.MaxValue ? declarationIndex : attr.Order;
            Min = attr.Min;
            Max = attr.Max;
            Choices = attr.Choices;
        }

        internal ParameterDescriptor(PropertyInfo property, OutputAttribute attr, int declarationIndex)
        {
            Property = property;
            Direction = ParameterDirection.Output;
            Name = string.IsNullOrEmpty(attr.Name) ? property.Name : attr.Name!;
            Description = attr.Description;
            ValueType = property.PropertyType;
            DefaultValue = null;
            IsOptional = false;
            Order = attr.Order == int.MaxValue ? declarationIndex : attr.Order;
            Min = double.NaN;
            Max = double.NaN;
            Choices = null;
        }

        public object? GetValue(OperatorBase op)
        {
            if (op == null) throw VisionException.NullArgument(nameof(op));
            return Property.GetValue(op);
        }

        /// <summary>값을 파라미터 타입으로 변환해 설정한다 (스크립트/Job 에서 오는 느슨한 타입 허용).</summary>
        public void SetValue(OperatorBase op, object? value)
        {
            if (op == null) throw VisionException.NullArgument(nameof(op));
            if (Direction != ParameterDirection.Input)
                throw new VisionException(ErrorCode.InvalidArgument, $"'{Name}' is an output parameter.");

            var converted = ConvertValue(value);
            CheckRange(converted);

            var setter = Property.GetSetMethod(nonPublic: true)
                         ?? throw new VisionException(ErrorCode.InvalidOperatorDefinition, $"Input '{Name}' has no setter.");
            setter.Invoke(op, new[] { converted });
        }

        /// <summary>기본값을 적용한다. 기본값이 없으면 false.</summary>
        public bool ApplyDefault(OperatorBase op)
        {
            if (DefaultValue == null)
                return false;
            SetValue(op, DefaultValue);
            return true;
        }

        private void CheckRange(object? value)
        {
            if (value == null || (double.IsNaN(Min) && double.IsNaN(Max)))
                return;

            double d;
            switch (value)
            {
                case TupleValue t when t.IsScalar && t.IsNumeric: d = t.D(); break;
                case IConvertible c when !(value is string) && !(value is bool): d = c.ToDouble(CultureInfo.InvariantCulture); break;
                default: return;
            }

            if (!double.IsNaN(Min) && d < Min) throw VisionException.OutOfRange(Name, d, Min, double.IsNaN(Max) ? "inf" : Max);
            if (!double.IsNaN(Max) && d > Max) throw VisionException.OutOfRange(Name, d, double.IsNaN(Min) ? "-inf" : Min, Max);
        }

        /// <summary>느슨한 스크립트 값을 파라미터 타입으로 변환한다.</summary>
        public object? ConvertValue(object? value)
        {
            var target = Nullable.GetUnderlyingType(ValueType) ?? ValueType;

            if (value == null)
            {
                if (IsOptional || !target.IsValueType)
                    return null;
                throw VisionException.NullArgument(Name);
            }

            if (target.IsInstanceOfType(value))
                return value;

            // Tuple → 스칼라
            if (value is TupleValue tuple)
            {
                if (target == typeof(TupleValue)) return tuple;
                if (!tuple.IsScalar)
                    throw new VisionException(ErrorCode.TypeMismatch, $"'{Name}' expects a scalar {DisplayNameOf(target)} but got a tuple of length {tuple.Length}.");
                value = tuple[0];
                if (target.IsInstanceOfType(value))
                    return value;
            }

            if (target == typeof(TupleValue))
                return TupleValue.From(value);

            if (target == typeof(string))
                return value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString();

            if (target.IsEnum)
            {
                if (value is string es)
                {
                    try { return Enum.Parse(target, es, ignoreCase: true); }
                    catch (ArgumentException) { throw VisionException.InvalidArgument(Name, $"'{es}' is not one of {string.Join("/", Enum.GetNames(target))}"); }
                }
                if (value is IConvertible ec)
                    return Enum.ToObject(target, ec.ToInt64(CultureInfo.InvariantCulture));
            }

            if (value is IConvertible conv && typeof(IConvertible).IsAssignableFrom(target))
            {
                try
                {
                    return Convert.ChangeType(conv, target, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                {
                    throw new VisionException(ErrorCode.TypeMismatch,
                        $"'{Name}' expects {DisplayNameOf(target)} but got '{value}' ({value.GetType().Name}).", ex);
                }
            }

            throw VisionException.TypeMismatch(Name, target, value.GetType());
        }

        public static string DisplayNameOf(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t == typeof(Image)) return "Image";
            if (t == typeof(Region)) return "Region";
            if (t == typeof(XldContour)) return "XLD";
            if (t == typeof(TupleValue)) return "Tuple";
            if (t == typeof(string)) return "string";
            if (t == typeof(double) || t == typeof(float)) return "real";
            if (t == typeof(int) || t == typeof(long)) return "integer";
            if (t == typeof(bool)) return "bool";
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ObjectArray<>))
                return DisplayNameOf(t.GetGenericArguments()[0]) + "[]";
            return t.Name;
        }

        public override string ToString()
            => $"{(Direction == ParameterDirection.Input ? "in" : "out")} {TypeDisplayName} {Name}"
               + (DefaultValue != null ? $" = {DefaultValue}" : "");
    }
}
