﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace DBFilesClient.NET
{
    public class DBCStorage<T> : Storage<T>, IEnumerable<T> where T : class, new()
    {
        #region Loading Information
        internal ConstructorInfo m_ctor;
        internal bool m_haveString;
        internal bool m_haveLazyCString;

        internal unsafe delegate void EntryLoader(byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings);
        internal EntryLoader m_loadMethod;
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new instance of <see cref="DBFilesClient.NET.DBCStorage&lt;T&gt;"/> class.
        /// </summary>
        public DBCStorage()
        {
            m_ctor = m_entryType.GetConstructor(Type.EmptyTypes);
            if (m_ctor == null)
                throw new InvalidOperationException("Cannot find default constructor for " + m_entryTypeName);

            var fields = GetFields();
            var properties = GetProperties();

            var fieldCount = fields.Length;
            m_fields = new EntryFieldInfo[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                var attr = fields[i].Value;

                var field = fields[i].Key;
                var fieldName = field.Name;

                var type = field.FieldType;
                if (type.IsEnum)
                    type = type.GetEnumUnderlyingType();

                if (i == 0 && type != s_intType && type != s_uintType)
                    throw new InvalidOperationException("First field of type " + m_entryTypeName + " must be Int32 or UInt32.");

                if (attr != null && attr.Option == StoragePresenceOption.UseProperty)
                {
                    // Property Detected
                    var propertyName = attr.PropertyName;
                    var property = properties.FirstOrDefault(prop => prop.Name == propertyName);
                    if (property == null)
                        throw new InvalidOperationException("Property " + propertyName + " for field " + fieldName
                            + " of class " + m_entryTypeName + " cannot be found.");

                    if (property.PropertyType != field.FieldType)
                        throw new InvalidOperationException("Property " + propertyName + " and field " + fieldName
                            + " of class " + m_entryTypeName + " must be of same types.");

                    m_fields[i].Property = property;
                    foreach (var accessor in property.GetAccessors())
                    {
                        if (accessor.ReturnType == field.FieldType)
                            m_fields[i].Getter = accessor;
                        else if (accessor.ReturnType == typeof(void))
                            m_fields[i].Setter = accessor;
                    }
                }
                else
                {
                    if (!field.IsPublic)
                    {
                        throw new InvalidOperationException(
                            "Field " + fieldName + " of type " + m_entryTypeName + " must be public. "
                            + "Use " + typeof(StoragePresenceAttribute).Name + " to exclude the field.");
                    }

                    m_fields[i].FieldInfo = field;
                }

                int elementCount = 1;

                if (type.IsArray)
                {
                    if (type.GetArrayRank() != 1)
                        throw new InvalidOperationException(
                            "Field " + fieldName + " of type " + m_entryTypeName + " cannot be a multi-dimensional array.");

                    if (attr == null || attr.ArraySize < 1)
                        throw new InvalidOperationException(
                            "Use " + typeof(StoragePresenceAttribute).Name + " to set number of elements of field "
                            + fieldName + " of type " + m_entryTypeName + ".");

                    m_fields[i].ArraySize = elementCount = attr.ArraySize;

                    type = type.GetElementType();
                }

                if (type == s_intType)
                {
                    m_fields[i].TypeId = StoredTypeId.Int32;
                    m_entrySize += 4 * elementCount;
                }
                else if (type == s_uintType)
                {
                    m_fields[i].TypeId = StoredTypeId.UInt32;
                    m_entrySize += 4 * elementCount;
                }
                else if (type == s_floatType)
                {
                    m_fields[i].TypeId = StoredTypeId.Single;
                    m_entrySize += 4 * elementCount;
                }
                else if (type == s_stringType)
                {
                    m_fields[i].TypeId = StoredTypeId.String;
                    m_entrySize += 4 * elementCount;
                    m_haveString = true;
                }
                else if (type == s_lazyCStringType)
                {
                    m_fields[i].TypeId = StoredTypeId.LazyCString;
                    m_entrySize += 4 * elementCount;
                    m_haveLazyCString = true;
                }
                else
                    throw new InvalidOperationException(
                        "Unknown field type " + type.FullName + " (field " + fieldName + ") of type " + m_entryTypeName + "."
                        );
            }

            GenerateIdGetter();
        }
        #endregion

        #region Generating Methods
        internal void EmitLoadOnStackTop(ILGenerator ilgen, StoredTypeId id, bool lastField)
        {
            //             0            1            2             3           4
            // args: byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings

            switch (id)
            {
                case StoredTypeId.Int32:
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data
                    break;
                case StoredTypeId.UInt32:
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data
                    ilgen.Emit(OpCodes.Ldind_U4);                           // stack = *(uint*)data
                    break;
                case StoredTypeId.Single:
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data
                    ilgen.Emit(OpCodes.Ldind_R4);                           // stack = *(float*)data
                    break;
                case StoredTypeId.String:
                    ilgen.Emit(OpCodes.Ldarg_2);                            // stack = pinnedPool
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, pinnedPool
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, pinnedPool
                    ilgen.Emit(OpCodes.Conv_I);                             // stack = (IntPtr)*(int*)data, pinnedPool
                    ilgen.Emit(OpCodes.Add);                                // stack = pinnedPool+*(int*)data
                    ilgen.Emit(OpCodes.Newobj, s_stringCtor);               // stack = string
                    break;
                case StoredTypeId.LazyCString:
                    ilgen.Emit(OpCodes.Ldloca_S, 0);                        // stack = &lazyCString
                    ilgen.Emit(OpCodes.Ldarg_1);                            // stack = pool, &lazyCString
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, pool, &lazyCString
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, pool, &lazyCString
                    ilgen.Emit(OpCodes.Call, s_lazyCStringCtor);            // stack =
                    ilgen.Emit(OpCodes.Ldarg_S, 4);                         // stack = ignoreLazyCStrings
                    var label = ilgen.DefineLabel();
                    ilgen.Emit(OpCodes.Brfalse, label);                     // stack =
                    ilgen.Emit(OpCodes.Ldloca_S, 0);                        // stack = &lazyCString
                    ilgen.Emit(OpCodes.Call, LazyCString.LoadStringInfo);   // stack =
                    ilgen.MarkLabel(label);
                    ilgen.Emit(OpCodes.Ldloc_0);                            // stack = lazyCString
                    break;
                default:
                    throw new InvalidOperationException();
            }

            if (!lastField)
            {
                ilgen.Emit(OpCodes.Ldarg_0);            // stack = data
                ilgen.Emit(OpCodes.Ldc_I4_4);           // stack = 4, data
                ilgen.Emit(OpCodes.Conv_I);             // stack = (IntPtr)4, data
                ilgen.Emit(OpCodes.Add);                // stack = data+4
                ilgen.Emit(OpCodes.Starg_S, 0);         // stack =
            }
        }

        internal void EmitLoadImm(ILGenerator ilgen, int value)
        {
            switch (value)
            {
                case 1:
                    ilgen.Emit(OpCodes.Ldc_I4_1);
                    break;
                case 2:
                    ilgen.Emit(OpCodes.Ldc_I4_2);
                    break;
                case 3:
                    ilgen.Emit(OpCodes.Ldc_I4_3);
                    break;
                case 4:
                    ilgen.Emit(OpCodes.Ldc_I4_4);
                    break;
                case 5:
                    ilgen.Emit(OpCodes.Ldc_I4_5);
                    break;
                case 6:
                    ilgen.Emit(OpCodes.Ldc_I4_6);
                    break;
                case 7:
                    ilgen.Emit(OpCodes.Ldc_I4_7);
                    break;
                case 8:
                    ilgen.Emit(OpCodes.Ldc_I4_8);
                    break;
                default:
                    ilgen.Emit(OpCodes.Ldc_I4, value);
                    break;
            }
        }

        internal void EmitArray(ILGenerator ilgen, Type type, StoredTypeId id, int count, Action<bool> elementGen, bool lastField)
        {
            EmitLoadImm(ilgen, count);
            ilgen.Emit(OpCodes.Newarr, type);

            for (int i = 0; i < count; ++i)
            {
                bool last = lastField && i + 1 >= count;

                ilgen.Emit(OpCodes.Dup);
                EmitLoadImm(ilgen, i);

                switch (id)
                {
                    case StoredTypeId.Int32:
                    case StoredTypeId.UInt32:
                        elementGen(last);
                        ilgen.Emit(OpCodes.Stelem_I4);
                        break;
                    case StoredTypeId.Single:
                        elementGen(last);
                        ilgen.Emit(OpCodes.Stelem_R4);
                        break;
                    case StoredTypeId.String:
                        elementGen(last);
                        ilgen.Emit(OpCodes.Stelem_Ref);
                        break;
                    case StoredTypeId.LazyCString:
                        ilgen.Emit(OpCodes.Ldelema, type);
                        elementGen(last);
                        ilgen.Emit(OpCodes.Stobj, type);
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        internal void EmitLoadField(ILGenerator ilgen, FieldInfo field, StoredTypeId id, bool lastField, int arraySize)
        {
            //             0            1            2             3           4
            // args: byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings

            ilgen.Emit(OpCodes.Ldarg_3);
            if (arraySize > 0)
                EmitArray(ilgen, field.FieldType.GetElementType(), id, arraySize, last => EmitLoadOnStackTop(ilgen, id, last), lastField);
            else
                EmitLoadOnStackTop(ilgen, id, lastField);
            ilgen.Emit(OpCodes.Stfld, field);
        }

        internal void EmitLoadProperty(ILGenerator ilgen, PropertyInfo property, MethodInfo setter, StoredTypeId id, bool lastField, int arraySize)
        {
            //             0            1            2             3           4
            // args: byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings

            ilgen.Emit(OpCodes.Ldarg_3);
            if (arraySize > 0)
                EmitArray(ilgen, property.PropertyType.GetElementType(), id, arraySize, last => EmitLoadOnStackTop(ilgen, id, last), lastField);
            else
                EmitLoadOnStackTop(ilgen, id, lastField);
            ilgen.Emit(OpCodes.Callvirt, setter);
        }

        internal void GenerateLoadMethod()
        {
            if (m_loadMethod != null)
                return;

            var method = new DynamicMethod(
                "EntryLoader_" + m_entryTypeName,
                typeof(void),
                new Type[] { typeof(byte*), typeof(byte[]), typeof(sbyte*), typeof(T), typeof(bool) },
                typeof(T).Module
                );

            var fieldCount = m_fields.Length;

            var ilgen = method.GetILGenerator(fieldCount * 20);

            if (m_haveLazyCString)
                ilgen.DeclareLocal(typeof(LazyCString), true);

            ilgen.Emit(OpCodes.Nop);

            for (int i = 0; i < fieldCount; i++)
            {
                var lastField = i + 1 >= fieldCount;
                var id = m_fields[i].TypeId;
                var fieldInfo = m_fields[i].FieldInfo;
                var propertyInfo = m_fields[i].Property;

                if (fieldInfo != null)
                    EmitLoadField(ilgen, fieldInfo, id, lastField, m_fields[i].ArraySize);
                else if (propertyInfo != null)
                {
                    var setter = m_fields[i].Setter;
                    if (setter != null)
                        EmitLoadProperty(ilgen, propertyInfo, setter, id, lastField, m_fields[i].ArraySize);
                    else
                        throw new InvalidOperationException(
                            "Setter of property " + propertyInfo.Name + " of class " + m_entryTypeName + " is inaccessible.");
                }
                else
                    throw new InvalidOperationException("Invalid field " + i + " in class " + m_entryTypeName + ".");
            }

            ilgen.Emit(OpCodes.Ret);

            m_loadMethod = (EntryLoader)method.CreateDelegate(typeof(EntryLoader));
        }
        #endregion

        #region Loading
        /// <summary>
        /// Loads the storage from a <see cref="System.IO.Stream"/>.
        /// </summary>
        /// <param name="stream">
        /// The <see cref="System.IO.Stream"/> from which the storage should be loaded.
        /// </param>
        public sealed override void Load(Stream stream)
        {
            Load(stream, LoadFlags.None);
        }

        /// <summary>
        /// Loads the storage from a <see cref="System.IO.Stream"/>.
        /// </summary>
        /// <param name="stream">
        /// The <see cref="System.IO.Stream"/> from which the storage should be loaded.
        /// </param>
        /// <param name="flags">
        /// The <see cref="DBFilesClient.NET.LoadFlags"/> to be used when loading.
        /// </param>
        public unsafe virtual void Load(Stream stream, LoadFlags flags)
        {
            GenerateLoadMethod();

            byte[] headerBytes;
            byte[] data;
            byte[] pool;

            fixed (byte* headerPtr = headerBytes = new byte[DBCHeader.Size])
            {
                if (stream.Read(headerBytes, 0, DBCHeader.Size) != DBCHeader.Size)
                    throw new IOException("Failed to read the DBC header.");

                var header = (DBCHeader*)headerPtr;
                if (!flags.HasFlag(LoadFlags.IgnoreWrongFourCC) && header->FourCC != 0x43424457)
                    throw new ArgumentException("This is not a valid DBC file.");

                if (header->RecordSize != m_entrySize)
                    throw new ArgumentException("This DBC file has wrong record size ("
                        + header->RecordSize + ", expected is " + m_entrySize + ").");

                m_records = header->Records;

                int index, size;

                index = 0;
                size = header->Records * header->RecordSize;
                data = new byte[size];
                while (index < size)
                    index += stream.Read(data, index, size - index);

                index = 0;
                size = header->StringPoolSize;
                pool = new byte[size];
                while (index < size)
                    index += stream.Read(pool, index, size - index);
            }

            fixed (byte* pdata_ = data)
            {
                byte* pdata = pdata_;

                if (m_records > 0)
                    this.Resize(*(uint*)pdata, *(uint*)(pdata + m_entrySize * (m_records - 1)));

                fixed (byte* ppool = m_haveString ? pool : null)
                {
                    bool ignoreLazyCStrings = !flags.HasFlag(LoadFlags.LazyCStrings);
                    try
                    {
                        for (int i = 0; i < m_records; i++)
                        {
                            var entry = (T)m_ctor.Invoke(null);

                            m_loadMethod(pdata, pool, (sbyte*)ppool, entry, ignoreLazyCStrings);

                            uint id = *(uint*)pdata;
                            m_entries[id - m_minId] = entry;

                            pdata += m_entrySize;
                        }
                    }
                    catch (FieldAccessException e)
                    {
                        throw new InvalidOperationException("Class " + m_entryTypeName + " must be public.", e);
                    }
                }
            }
        }
        #endregion
    }
}
