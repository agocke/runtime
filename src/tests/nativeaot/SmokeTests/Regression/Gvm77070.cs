using System.Collections.Generic;

// Regression test for https://github.com/dotnet/runtime/issues/77070
public static class Gvm77070
{
    public static int Run()
    {
        var deserializer = new JsonDeserializer();
        var visitor = new Release.SerdeVisitor();
        deserializer.DeserializeDictionary<Release, Release.SerdeVisitor>(visitor);
        return 100;
    }

    public partial record struct Release
    {
        public sealed class SerdeVisitor : IDeserializeVisitor<Release>
        {
            void IDeserializeVisitor<Release>.VisitDictionary<D>(ref D d)
            {
                d.GetNextValue<DictWrap<string, StringWrap, string>>();
            }
        }
    }

    public record StringWrap(string Value)
        : IDeserialize
    {
        public static void Deserialize<D>(ref D deserializer)
            where D : IDeserializer
        {
        }
    }

    public readonly struct DictWrap<TKey, TKeyWrap, TValue> : IDeserialize
        where TKey : notnull
        where TKeyWrap : IDeserialize
    {
        static void IDeserialize.Deserialize<D>(ref D deserializer)
        {
            deserializer.DeserializeDictionary<Dictionary<TKey, TValue>, Visitor>(new Visitor());
        }
        private sealed class Visitor : IDeserializeVisitor<Dictionary<TKey, TValue>>
        {
            void IDeserializeVisitor<Dictionary<TKey, TValue>>.VisitDictionary<D>(ref D d)
            {
                d.GetNextValue<TKeyWrap>();
            }
        }
    }

    public interface IDeserializeVisitor<T>
    {
        void VisitDictionary<D>(ref D d) where D : IDeserializeDictionary;
    }

    public interface IDeserialize
    {
        static abstract void Deserialize<D>(ref D deserializer) where D : IDeserializer;
    }

    public interface IDeserializeDictionary
    {
        void GetNextValue<D>() where D : IDeserialize;
    }

    public interface IDeserializer
    {
        void DeserializeDictionary<T, V>(V v) where V : class, IDeserializeVisitor<T>;
    }
    public sealed partial class JsonDeserializer : IDeserializer
    {
        public void DeserializeDictionary<T, V>(V v) where V : class, IDeserializeVisitor<T>
        {
            var map = new DeDictionary(this);
            v.VisitDictionary(ref map);
        }

        private struct DeDictionary : IDeserializeDictionary
        {
            private JsonDeserializer _deserializer;
            public DeDictionary(JsonDeserializer de)
            {
                _deserializer = de;
            }

            public void GetNextValue<D>() where D : IDeserialize
            {
                D.Deserialize(ref _deserializer);
            }
        }
    }
}