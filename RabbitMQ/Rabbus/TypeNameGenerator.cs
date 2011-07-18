﻿using System;

namespace Rabbus
{
    public class TypeNameGenerator : ITypeNameGenerator
    {
        public string Generate<T>()
        {
            return Generate(typeof(T));
        }

        public string Generate(Type type)
        {
            var fullName = type.AssemblyQualifiedName;
            return fullName.Substring(0, fullName.IndexOf(", Version="));
        }
    }
}