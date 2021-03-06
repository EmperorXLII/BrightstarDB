﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using BrightstarDB.Client;

namespace BrightstarDB.EntityFramework
{
    /// <summary>
    /// The base class for the Brightstar Entity Framework's generated domain object classes
    /// </summary>
    public class BrightstarEntityObject : IEntityObject, INotifyPropertyChanged
    {
        private BrightstarEntityContext _context;
        private string _identity;
        private IDataObject _dataObject;

        internal IDataObject DataObject
        {
            get { return _dataObject; }
            set
            {
                if (value != null) _identity = value.Identity;
                _dataObject = value;
            }
        }

        private readonly Dictionary<string, object> _currentItemValues = new Dictionary<string, object>();
        private readonly Dictionary<string, BrightstarEntityObject> _currentPropertyValues = new Dictionary<string, BrightstarEntityObject>();
        private readonly Dictionary<string, IBrightstarEntityCollection> _currentPropertyCollections = new Dictionary<string, IBrightstarEntityCollection>();

        /// <summary>
        /// Creates a domain object
        /// </summary>
        /// <param name="context">The context that the domain object is attached to</param>
        /// <param name="dataObject">The underlying Brightstar data object</param>
        public BrightstarEntityObject(BrightstarEntityContext context, IDataObject dataObject)
        {
            _context = context;
            DataObject = dataObject;
            _context.TrackObject(this);
            TriggerCreatedEvent(context);
        }

        /// <summary>
        /// Creates a domain object bound to a specific type
        /// </summary>
        /// <param name="context">The context that the new domain object will be attached to</param>
        /// <param name="entityType">The implementation class for the domain object</param>
        public BrightstarEntityObject(BrightstarEntityContext context, Type entityType)
        {
            _context = context;
            DataObject = context.CreateDataObject(entityType);
            _context.TrackObject(this);
            TriggerCreatedEvent(context);
        }

        /// <summary>
        /// Creates an entity object that is attached to a <see cref="BrightstarEntityContext"/>
        /// and bound to a resource
        /// </summary>
        /// <param name="context">The context that the object will be attached to</param>
        /// <param name="identity">The identity that the object will be bound to</param>
        public BrightstarEntityObject(BrightstarEntityContext context, Uri identity)
        {
            _context = context;
            DataObject = _context.GetDataObject(identity, false);
            _context.TrackObject(this);
            TriggerCreatedEvent(context);
        }

        /// <summary>
        /// Creates a new, unattached BrightstarEntityObject
        /// </summary>
        /// <remarks>The <see cref="Context"/> and <see cref="Identity"/> properties
        /// of the object must be set before attempting to get or set any of its other properties.</remarks>
        public BrightstarEntityObject()
        {
            TriggerCreatedEvent(null);
        }

        #region Implementation of IEntityObject

        /// <summary>
        /// Returns true if the object is currently attached to a context
        /// </summary>
        public bool IsAttached
        {
            get { return ((_context != null) && (DataObject != null)); }
        }

        /// <summary>
        /// Get or set the context that the item is currently attached to
        /// </summary>
        /// <remarks>Changing the context that an entity is attached to will cause any unsaved changes to that
        /// entity to be lost.</remarks>
        public EntityContext Context
        {
            get { return _context; }
            set
            {
                if (value == null) { _context = null; }
                else
                {
                    var brightstarEntityContext = value as BrightstarEntityContext;
                    if (brightstarEntityContext == null)
                    {
                        throw new ArgumentException(
                            "The EntityContext for a BrightstarEntityObject must be a BrightstarEntityContext.");
                    }

                    Attach(brightstarEntityContext);
                }
            }
        }

        /// <summary>
        /// Get or set the URI of the resource that this entity object is attached to
        /// </summary>
        /// <remarks>Once a BrightstarEntityObject is attached to a context (either by calling the
        /// <see cref="Attach"/> method or setting the <see cref="Context"/> and <see cref="Identity"/> 
        /// properties to non-null values), the identity cannot be modified.</remarks>
        /// <exception cref="InvalidOperationException">Thrown if an attempt is made to set this
        /// property with the object attached to a context.</exception>
        protected string Identity
        {
            get { return  _identity; }
            set
            {
                if (value == _identity) return;
                if (DataObject != null)
                {
                    DataObject = DataObject.UpdateIdentity(value, true);
                    //throw new InvalidOperationException("Cannot modify the identity of an attached BrightstarEntityObject");
                }
                _identity = value;
                if (_context != null)
                {
                    Attach(_context);
                }
                else
                {
                    // Store the identity until we attach to a context
                    _identity = value;
                }
            }
        }

        /// <summary>
        /// Flag indicating if this entity has been locally modified
        /// </summary>
        public bool IsModified 
        { 
            get
            {
                return DataObject != null && DataObject.IsModified;
            }
        }
        
        /// <summary>
        /// Sets the key for this object
        /// </summary>
        /// <param name="key">The new object identity</param>
        /// <remarks>If the entity definition interface has a <see cref="IdentifierAttribute"/> on it,
        /// then the full identity of the object will be the value of the <see cref="IdentifierAttribute.BaseAddress"/> 
        /// property followed by the <paramref name="key"/> parameter value, otherwise the default <see cref="Constants.GeneratedUriPrefix"/>
        /// is prepended to make a full URI.</remarks>
        protected void SetKey(string key)
        {
            if (String.IsNullOrEmpty(key))
            {
                throw new EntityKeyRequiredException();
            }
            var baseUri = GetIdentityBase();
            var identity = String.IsNullOrEmpty(baseUri) ? key : baseUri + key;
            if (!identity.Equals(Identity))
            {
                if (_context != null && _context.TrackedObjects.Any(x => x.Identity.Equals(identity) && !ReferenceEquals(x, this)))
                {
                    throw new UniqueConstraintViolationException();
                }
                Identity = identity;
            }
        }

        /// <summary>
        /// Returns the key for this object.
        /// </summary>
        /// <returns>The object key</returns>
        /// <remarks>The string returned by this method is the unique key part of the resource address of
        /// the underlying data object.</remarks>
        public string GetKey()
        {
            var baseUri = GetIdentityBase();
            var identity = Identity;
            if (String.IsNullOrEmpty(identity) || String.IsNullOrEmpty(baseUri)) return identity;
            return identity.StartsWith(baseUri) ? identity.Substring(baseUri.Length) : identity;
        }

        internal string AssertIdentity(string idOrAddress = null)
        {
            if (DataObject != null) return DataObject.Identity;
            if (String.IsNullOrEmpty(idOrAddress))
            {
                idOrAddress = GenerateEntityKey();
            }
            if (String.IsNullOrEmpty(_identity))
            {
                if (!String.IsNullOrEmpty(GetIdentityBase()))
                {
                    SetKey(idOrAddress);
                }
                else
                {
                    SetKey(Constants.GeneratedUriPrefix + idOrAddress);
                }
            }
            return GetKey();
        }

        private string GenerateEntityKey()
        {
            var identityCacheInfo = EntityMappingStore.GetIdentityInfo(GetType());
            if (identityCacheInfo != null && identityCacheInfo.KeyProperties != null)
            {
                // Generate the key string
                var values = new object[identityCacheInfo.KeyProperties.Length];
                for (var i = 0; i < identityCacheInfo.KeyProperties.Length; i++)
                {
                    if (!_currentItemValues.TryGetValue(identityCacheInfo.KeyProperties[i].Name, out values[i]))
                    {
                        values[i] = identityCacheInfo.KeyProperties[i].GetValue(this, null);
                    }
                }
                return identityCacheInfo.KeyConverter.GenerateKey(values, identityCacheInfo.KeySeparator, GetType());
            }
            return Guid.NewGuid().ToString();
        }

        internal string GetIdentityBase()
        {
            var identityCacheInfo = EntityMappingStore.GetIdentityInfo(GetType());
            return identityCacheInfo.BaseUri;
        }


        /// <summary>
        /// Returns the value of a property of the object
        /// </summary>
        /// <typeparam name="T">The type of item to return</typeparam>
        /// <param name="propertyName">The name of the domain object property to inspect</param>
        /// <returns>The value to provide for the property</returns>
        public T GetRelatedProperty<T>(string propertyName)
        {
            if (!IsAttached)
            {
                object value;
                if (_currentItemValues.TryGetValue(propertyName, out value) &&
                    typeof(T).IsAssignableFrom(value.GetType()))
                {
                    return (T) value;
                }
                return default(T);
                //return new T[0].FirstOrDefault(); // TODO : Find a better way to get the default value
            }
            var propertyType = GetPropertyUri(propertyName);
            if (typeof(T).GetTypeInfo().IsEnum)
            {
                var enumValue = DataObject.GetPropertyValue(propertyType);
                var valueDefinedByEnum = enumValue != null && Enum.IsDefined(typeof(T), enumValue);
                var isFlagsEnum = typeof(T).GetTypeInfo().GetCustomAttributes(typeof(FlagsAttribute), true).Any();
                return (enumValue != null && (valueDefinedByEnum || isFlagsEnum))
                           ? (T)Enum.ToObject(typeof(T), DataObject.GetPropertyValue(propertyType))
                           : default(T);
            }
            if (typeof (T) == typeof (Uri))
            {
                var value = DataObject.GetPropertyValue(propertyType) as IDataObject;
                object ret = value == null ? null : new Uri(value.Identity);
                return (T) ret;
            }
            if (typeof(T).IsNullable() && typeof(T).GetGenericArguments()[0].GetTypeInfo().IsEnum)
            {
                var enumType = typeof (T).GetGenericArguments()[0];
                var enumValue = DataObject.GetPropertyValue(propertyType);
                var valueDefinedByEnum = enumValue!=null && Enum.IsDefined(enumType, enumValue);
                var isFlagsEnum = enumType.GetTypeInfo().GetCustomAttributes(typeof(FlagsAttribute), true).Any();
                return (enumValue != null && (valueDefinedByEnum||isFlagsEnum))
                           ? (T) Enum.ToObject(enumType, enumValue)
                           : default(T);
            }
            object returnValue = DataObject.GetPropertyValue(propertyType);
            if (returnValue == null && typeof(T).IsValueType()) return default(T);
            if (typeof(T) == typeof(String) && returnValue!=null)
            {
                object o = returnValue.ToString();
                return (T) o;
            }
            return (T) returnValue;
        }

        /// <summary>
        /// Updates the property of a domain object
        /// </summary>
        /// <param name="propertyName">The property to be updated</param>
        /// <param name="value">The new property value</param>
        public void SetRelatedProperty(string propertyName, object value)
        {
            var currentValue = GetRelatedProperty<object>(propertyName);
            if (SafeEquals(value, currentValue))
            {
                // Don't do anything if there is no change to the property value
                return;
            }
            if (!IsAttached)
            {
                _currentItemValues[propertyName] = value;
            }
            else
            {
                var propertyType = GetPropertyUri(propertyName);
                if (value == null)
                {
                    DataObject.RemovePropertiesOfType(propertyType);
                }
                else
                {
                    if (value is System.Enum)
                    {
                        DataObject.SetProperty(propertyType, (int) value);
                    }
                    else
                    {
                        DataObject.SetProperty(propertyType, value);
                    }
                }
            }
            OnPropertyChanged(propertyName);
        }

        private bool SafeEquals(object x, object y)
        {
            var xIsDefault = IsDefaultValue(x);
            var yIsDefault = IsDefaultValue(y);
            if (xIsDefault && yIsDefault)
            {
                return true;
            }
            if (xIsDefault || yIsDefault) return false;
            return x.Equals(y);
        }

        private bool IsDefaultValue(object o)
        {
            if (o == null) return true;
            return o == o.GetType().GetDefaultValue();
        }

        private PropertyHint GetPropertyHint(string propertyName)
        {
            var property = GetType().GetProperty(propertyName);
            if (property == null)
            {
                throw new ArgumentException(String.Format("Cannot find property named '{0}' on type '{1}'", propertyName, GetType().FullName));
            }
            var propertyHint = _context.GetPropertyHint(property);
            if (propertyHint == null)
            {
                throw new EntityFrameworkException(
                    String.Format("No property mapping hint found for property named '{0}' on type '{1}'", propertyName, GetType().FullName));
            }
            return propertyHint;
        }

        private string GetPropertyUri(string propertyName)
        {
            // TODO: Cache this statically for the class?
            var propertyHint = GetPropertyHint(propertyName);
            if (propertyHint.MappingType != PropertyMappingType.Property)
            {
                throw new ArgumentException(String.Format("Property '{0}' on type '{1}' does not map to a literal property type", propertyName, GetType().FullName));
            }
            return propertyHint.SchemaTypeUri;
        }

        /// <summary>
        /// Invoked by generated class prior to changing the value of a scalar property
        /// </summary>
        /// <param name="propertyName">The name of the property being modified</param>
        /// <param name="newValue">The new value that will be assigned to the property</param>
        public void ReportPropertyChanging(string propertyName, object newValue)
        {
            // No-op
        }

        /// <summary>
        /// Invoked by the generated class after the value of a scalar property has been modified
        /// </summary>
        /// <param name="propertyName">The name of the property that has been modified</param>
        public void ReportPropertyChanged(string propertyName)
        {
            if (IsAttached)
            {
                var property = GetType().GetProperty(propertyName);
                if(property == null)
                {
                    throw new ArgumentException(String.Format("Cannot find property named '{0}' on type '{1}'", propertyName, GetType().FullName));
                }
                var propertyHint = _context.GetPropertyHint(property);
                var propertyValue = property.GetValue(this, null);
                switch (propertyHint.MappingType)
                {
                    case PropertyMappingType.Property:
                        DataObject.SetProperty(propertyHint.SchemaTypeUri, propertyValue);
                        break;
                    default:
                        throw new EntityFrameworkException(
                            String.Format("The property mapping type {0} is not supported by the Brightstar EntityFramework", propertyHint.MappingType));
                }
            }
        }

        /// <summary>
        /// Invoked by the generated class to change the value of a property whose type
        /// is another entity
        /// </summary>
        /// <typeparam name="T">The type of the related entity</typeparam>
        /// <param name="propertyName">The name of the property that represents the relationship</param>
        /// <param name="value">The new related entity</param>
        /// <exception cref="EntityFrameworkException">Thrown if this object is not currently attached to a context</exception>
        public void SetRelatedObject<T>(string propertyName, T value) where T : class
        {
            if (!IsAttached)
            {
                if (_currentPropertyValues.ContainsKey(propertyName) &&
                                    SafeEquals(_currentPropertyValues[propertyName], value))
                {
                    return;
                }
                _currentItemValues[propertyName] = value;
                OnPropertyChanged(propertyName);
                return;
            }

            var entity = ValidateAndAttach(value, propertyName);
            if (_currentPropertyValues.ContainsKey(propertyName) &&
                SafeEquals(_currentPropertyValues[propertyName], entity))
            {
                return;
            }

            var propertyHint = GetPropertyHint(propertyName);
            bool isSingleValuedForwardProperty = false;
            // Remove existing arcs
            if (propertyHint.MappingType == PropertyMappingType.Arc)
            {
                var existingDataObject = DataObject.GetPropertyValue(propertyHint.SchemaTypeUri) as DataObject;
                if (existingDataObject != null)
                {
                    _context.RemoveArc(DataObject, propertyHint.SchemaTypeUri, existingDataObject);
                }
                // If the value entity has a single-value inverse properties of this type then all arcs needs removing
                var invArcProperties = _context.GetInverseArcProperties(typeof (T), propertyHint.SchemaTypeUri).ToList();
                if (invArcProperties.Any() && invArcProperties.All(x=>!(IsCollectionType(x.PropertyType))))
                {
                    foreach (var existingValueRef in entity.DataObject.GetInverseOf(propertyHint.SchemaTypeUri).ToList())
                    {
                        _context.RemoveArc(existingValueRef, propertyHint.SchemaTypeUri, entity.DataObject);
                    }
                }

                // Clean up any inverse collection properties for this property
                foreach (var p in invArcProperties)
                {
                    if (existingDataObject != null)
                    {
                        foreach (var trackedObject in _context.GetTrackedObjects(existingDataObject))
                        {
                            var otherCollection = p.GetValue(trackedObject, null) as IBrightstarEntityCollection;
                            if (otherCollection != null) otherCollection.RemoveFromLoadedObjects(_identity);
                        }
                    }
                }

            }
            else if (propertyHint.MappingType == PropertyMappingType.InverseArc)
            {
                var props = _context.GetArcProperties(typeof(T), propertyHint.SchemaTypeUri).ToList();
                foreach (var existingDataObject in DataObject.GetInverseOf(propertyHint.SchemaTypeUri).ToList())
                {
                    _context.RemoveArc(existingDataObject, propertyHint.SchemaTypeUri, DataObject);
                    // Clean up any forward collection properties for this property
                    foreach (var trackedObject in _context.GetTrackedObjects(existingDataObject))
                    {
                        foreach (var p in props)
                        {
                            var collection = p.GetValue(trackedObject, null) as IBrightstarEntityCollection;
                            if (collection != null) collection.RemoveFromLoadedObjects(_identity);
                        }
                    }
                }
                // If the value entity has a single-value forward property of this type then all existing arcs from that value need removing
                if (props.Any() && props.All(x => !(IsCollectionType(x.PropertyType))))
                {
                    isSingleValuedForwardProperty = true;
                    foreach (var existingValueRef in entity.DataObject.GetPropertyValues(propertyHint.SchemaTypeUri).OfType<IDataObject>().ToList())
                    {
                        _context.RemoveArc(entity.DataObject, propertyHint.SchemaTypeUri, existingValueRef);
                    }
                }
            }

            if (entity == null)
            {
                // No new value so just record a null for this property and return
                _currentPropertyValues[propertyName] = null;
                OnPropertyChanged(propertyName);
                return;
            }

            // Create new arc
            if (propertyHint.MappingType == PropertyMappingType.Arc)
            {
                _context.AddArc(this, propertyHint.SchemaTypeUri, entity, true);
            }
            else if (propertyHint.MappingType == PropertyMappingType.InverseArc)
            {
                _context.AddArc(entity, propertyHint.SchemaTypeUri, this, isSingleValuedForwardProperty);
            }

            // Update cache
            _currentPropertyValues[propertyName] = entity;

            OnPropertyChanged(propertyName);
        }

        private static bool IsCollectionType (Type t)
        {
            if(t.IsGenericType())
            {
                var typeDef = t.GetGenericTypeDefinition();
                var ret = typeDef.GetTypeInfo().IsSubclassOf(typeof (ICollection<>)) || typeDef.Equals(typeof(ICollection<>));
                return ret;
            }
            return t.GetTypeInfo().IsSubclassOf(typeof (ICollection));
        }

        private static bool IsLiteralsCollection(Type t)
        {
            if (t.IsGenericType())
            {
                var typeDef = t.GetGenericTypeDefinition();
                return typeDef.Equals(typeof(LiteralsCollection<>));
            }
            return false;
        }

        private BrightstarEntityObject ValidateAndAttach(object value, string propertyName)
        {
            if (value == null) return null;
            var entity = value as BrightstarEntityObject;
            if (entity == null)
            {
                throw new EntityFrameworkException(
                    String.Format("The value of the property '{0}' must be a class that extends '{1}'", propertyName,
                                  typeof(BrightstarEntityObject).FullName));
            }
            if (!entity.IsAttached)
            {
                entity.Attach(_context);
            }
            return entity;
        }

        /// <summary>
        /// Invoked by the generated class to retrieve the value of a property whose
        /// type is another entity
        /// </summary>
        /// <typeparam name="T">The type of the related entity</typeparam>
        /// <param name="propertyName">The name of the property that represents the relationship</param>
        /// <returns>The related entity or null if there is no related entity</returns>
        /// <exception cref="EntityFrameworkException">Thrown if this object is not currently attached to a context.</exception>
        public T GetRelatedObject<T>(string propertyName) where T: class
        {
            if (!IsAttached)
            {
                object value;
                if (_currentItemValues.TryGetValue(propertyName, out value) &&
                    value is T)
                {
                    return value as T;
                }
                return null;
            }

            BrightstarEntityObject currentValue;
            if (_currentPropertyValues.TryGetValue(propertyName, out currentValue))
            {
                return currentValue as T;
            }
            var propertyHint = GetPropertyHint(propertyName);
            if (propertyHint.MappingType == PropertyMappingType.Arc)
            {
                var dataObject = DataObject.GetPropertyValues(propertyHint.SchemaTypeUri).OfType<IDataObject>().FirstOrDefault();
                return _context.Bind<T>(dataObject);
            }
            if (propertyHint.MappingType == PropertyMappingType.InverseArc)
            {
                var dataObject = DataObject.GetInverseOf(propertyHint.SchemaTypeUri).FirstOrDefault();
                return dataObject == null ? null : _context.Bind<T>(dataObject);
            }
            throw new EntityFrameworkException(
                "Cannot retrieve related entity for property '{0}' on type '{1}' because the property is mapped as '{2}'",
                propertyName, GetType().FullName, propertyHint.MappingType);
        }

        /// <summary>
        /// Returns the collection of literal values for a property of this entity
        /// </summary>
        /// <typeparam name="T">The type of literal value to return</typeparam>
        /// <param name="propertyName">The name of the property</param>
        /// <returns>The collection of literal values</returns>
        public LiteralsCollection<T> GetRelatedLiteralPropertiesCollection<T>(string propertyName)
        {
            if (!IsAttached)
            {
                object value;
                if (_currentItemValues.TryGetValue(propertyName, out value) &&
                    value is LiteralsCollection<T>)
                {
                    return value as LiteralsCollection<T>;
                }
                var literalsCollection = new LiteralsCollection<T>(new T[0]);
                _currentItemValues[propertyName] = literalsCollection;
                return literalsCollection;
            }

            if (_currentItemValues.ContainsKey(propertyName))
            {
                return _currentItemValues[propertyName] as LiteralsCollection<T>;
            }

            var propertyHint = GetPropertyHint(propertyName);
            if (propertyHint.MappingType != PropertyMappingType.Property)
            {
                throw new EntityFrameworkException(
                    "Cannot retrieve related literals for property '{0}' on type '{1}' because the property is mapped as '{2}'",
                    propertyName, GetType().FullName, propertyHint.MappingType);
            }
            var properties = new LiteralsCollection<T>(this, propertyHint.SchemaTypeUri);
            _currentItemValues[propertyName] = properties;
            return properties;
        }

        ///<summary>
        /// Sets a collection of literals as a property value on this entity.
        ///</summary>
        ///<param name="propertyName">Property Name</param>
        ///<param name="value">A collection of literals that are the property value.</param>
        ///<typeparam name="T">The type of the literal</typeparam>
        ///<exception cref="EntityFrameworkException">Thrown if the named property is not mapped.</exception>
        public void SetRelatedLiteralPropertiesCollection<T>(string propertyName, ICollection<T> value)
        {
            if (!IsAttached)
            {
                var literalsCollection = new LiteralsCollection<T>(value);
                _currentItemValues[propertyName] = literalsCollection;
            }
            else
            {
                object existing;
                LiteralsCollection<T> literalsCollection;
                if (_currentItemValues.TryGetValue(propertyName, out existing) && existing is LiteralsCollection<T>)
                {
                    literalsCollection = existing as LiteralsCollection<T>;
                }
                else
                {
                    var propertyHint = GetPropertyHint(propertyName);
                    if (propertyHint.MappingType != PropertyMappingType.Property)
                    {
                        throw new EntityFrameworkException(
                            "Cannot set related literals for property '{0}' on type '{1}' because the property is mapped as '{2}'",
                            propertyName, GetType().FullName, propertyHint.MappingType);
                    }
                    literalsCollection = new LiteralsCollection<T>(this, propertyHint.SchemaTypeUri);
                    _currentItemValues[propertyName] = literalsCollection;
                }
                literalsCollection.Clear();
                foreach (var item in value) literalsCollection.Add(item);
            }
        }

        /// <summary>
        /// Invoked by the generated class to retrieve the collection of related entities
        /// for a specific property
        /// </summary>
        /// <typeparam name="T">The type of entity expected</typeparam>
        /// <param name="propertyName">The name of the property</param>
        /// <returns></returns>
        public IEntityCollection<T> GetRelatedObjects<T>(string propertyName) where T : class
        {
            if (!IsAttached)
            {
                object value;
                if (_currentItemValues.TryGetValue(propertyName, out value) &&
                    value is IEntityCollection<T>)
                {
                    return value as IEntityCollection<T>;
                }
                var entityCollection = new UnattachedEntityCollection<T>();
                _currentItemValues[propertyName] = entityCollection;
                return entityCollection;
            }

            if (_currentPropertyCollections.ContainsKey(propertyName))
            {
                return _currentPropertyCollections[propertyName] as IEntityCollection<T>;
            }
            IEntityCollection<T> ret;
            var propertyHint = GetPropertyHint(propertyName);
            if (propertyHint.MappingType == PropertyMappingType.Arc)
            {
                ret = new BrightstarEntityCollection<T>(_context, this, propertyHint.SchemaTypeUri);
            }
            else if (propertyHint.MappingType == PropertyMappingType.InverseArc)
            {
                ret = new BrightstarEntityCollection<T>(_context, this, propertyHint.SchemaTypeUri, true);
            }
            else
            {
                throw new EntityFrameworkException(
                    "Cannot retrieve related entities for property '{0}' on type '{1}' because the property is mapped as '{2}'",
                    propertyName, GetType().FullName, propertyHint.MappingType);
            }
            _currentPropertyCollections[propertyName] = ret as IBrightstarEntityCollection;
            return ret;
        }

        /// <summary>
        /// Sets the collection of related entities for a specific property
        /// </summary>
        /// <typeparam name="T">The related entity type</typeparam>
        /// <param name="propertyName">The name of the property to be updated</param>
        /// <param name="relatedObjects">The new collection of related entities</param>
        public void SetRelatedObjects<T>(string propertyName, ICollection<T> relatedObjects) where T: class
        {
            if (!IsAttached)
            {
                var current = GetRelatedObjects<T>(propertyName);
                current.Set(relatedObjects);
                return;
            }

            if (_currentPropertyCollections.ContainsKey(propertyName))
            {
                var currentCollection = _currentPropertyCollections[propertyName] as IEntityCollection<T>;
                currentCollection.Set(relatedObjects);
            }
            else
            {
                var propertyHint = GetPropertyHint(propertyName);
                if (propertyHint.MappingType == PropertyMappingType.Arc)
                {
                    DataObject.RemovePropertiesOfType(propertyHint.SchemaTypeUri);
                    if (relatedObjects.Any(r=>!(r is BrightstarEntityObject)))
                    {
                        throw new ArgumentException("Related objects must all extend BrightstarDB.EntityFramework.BrightstarEntityObject");
                    }
                    foreach(var i in relatedObjects.Cast<BrightstarEntityObject>())
                    {
                        if (!i.IsAttached)
                        {
                            i.AssertIdentity();
                            i.Attach(_context);
                        }
                        DataObject.AddProperty(propertyHint.SchemaTypeUri, i.DataObject);
                    }
                }
            }
        }

        /// <summary>
        /// Attaches the object to the specified context
        /// </summary>
        /// <param name="context"></param>
        /// <param name="overwriteExisting"></param>
        public void Attach(EntityContext context, bool overwriteExisting = false)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (!(context is BrightstarEntityContext))
            {
                throw new ArgumentException(
                    String.Format("An object of type {0} can only be attached to a context that extends {1}",
                                  GetType().FullName, typeof (BrightstarEntityContext).FullName));
            }
            if (IsAttached)
            {
                if (!context.Equals(_context))
                {
                    _context.UntrackObject(this);
                }
            }
            _context = context as BrightstarEntityContext;
            if (_identity == null) AssertIdentity();
            if (DataObject == null && _identity != null)
            {
                DataObject = _context.GetDataObject(new Uri(_identity), false);
                var identityInfo = EntityMappingStore.GetIdentityInfo(GetType());
                if (identityInfo != null && identityInfo.EnforceClassUniqueConstraint && !overwriteExisting)
                {
                    _context.EnforceClassUniqueConstraint(_identity, EntityMappingStore.MapTypeToUris(GetType()));
                }
                foreach(var typeUri in EntityMappingStore.MapTypeToUris(GetType()))
                {
                    if (!String.IsNullOrEmpty(typeUri))
                    {
                        var typeDo = _context.GetDataObject(new Uri(typeUri), false);
                        if (typeDo != null)
                        {
                            DataObject.AddProperty(Client.DataObject.TypeDataObject, typeDo);
                        }
                    }
                }
            }
            else if (DataObject != null)
            {
                // Refresh the data object as a copy created in this context
                _context.AttachDataObject(this);
            }
            _context.TrackObject(this);

            if (_currentItemValues != null)
            {
                foreach (var propertyName in _currentItemValues.Keys.ToList())
                {
                    PropertyInfo p = GetType().GetProperty(propertyName);
                    p.SetValue(this, _currentItemValues[propertyName], null);
                }
                _currentItemValues.Clear();
            }
        }

        /// <summary>
        /// Removes the object from its current context
        /// </summary>
        public void Detach()
        {
            if (_context == null) return;
            _context.UntrackObject(this);
            _context = null;
        }

        #endregion

        /// <summary>
        /// Returns an new entity object bound to the same resource as this entity object
        /// </summary>
        /// <typeparam name="T">The entity definition interface that the new entity object should implement</typeparam>
        /// <returns>A new object that implements the entity definition interface specified by the type parameter <typeparamref name="T"/>
        /// and that is bound to the same underlying resource as this entity object.</returns>
        /// <remarks>This method adds one or more type properties to the underlying resource so that it becomes a resource
        /// both of the type of this entity object and of the type of entity object that it becomes. After committing changes,
        /// the resource can then be accessed through either entity collection on the context object.</remarks>
        /// <exception cref="MappingNotFoundException">Raised if <typeparamref name="T"/> is not a registered entity definition interface type.</exception>
        public T Become<T>() 
        {
            return _context.Become<T>(this);
        }

        /// <summary>
        /// Removes a type binding from this entity object
        /// </summary>
        /// <typeparam name="T">The entity definition interface that defines the type that should be removed from this entity object</typeparam>
        /// <remarks>This method removes only the direct type of the specified entity definition, it does not remove the super-types of that
        /// entity definition as they may be shared by the other types of the resource. After commiting changes, the resource will no longer
        /// be accessible through the collection of entities of type <typeparamref name="T"/> on the context object.</remarks>
        /// <exception cref="MappingNotFoundException">Raised if <typeparamref name="T"/> is not a registered entity definition interface type.</exception>
        public void Unbecome<T>() 
        {
            _context.Unbecome<T>(this);
        }

        #region Equality and HashCode overrides

        /// <summary>
        /// Serves as a hash function for a particular type. 
        /// </summary>
        /// <returns>
        /// A hash code for the current <see cref="T:System.Object"/>.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        public override int GetHashCode()
        {
            return _identity.GetHashCode();
        }

        /// <summary>
        /// Determines whether the specified <see cref="T:System.Object"/> is equal to the current <see cref="T:System.Object"/>.
        /// </summary>
        /// <returns>
        /// true if the specified <see cref="T:System.Object"/> is equal to the current <see cref="T:System.Object"/>; otherwise, false.
        /// </returns>
        /// <param name="obj">The <see cref="T:System.Object"/> to compare with the current <see cref="T:System.Object"/>. </param><filterpriority>2</filterpriority>
        public override bool Equals(object obj)
        {
            var other = obj as BrightstarEntityObject;
            return other != null && IsAttached && other.IsAttached && other._identity.Equals(_identity);
        }
        #endregion

        internal void UpdatePropertyCollection(string propertyName, BrightstarEntityObject objectToAdd, string identityToRemove)
        {
            IBrightstarEntityCollection cachedValue;
            if (_currentPropertyCollections.TryGetValue(propertyName, out cachedValue))
            {
                if (identityToRemove != null) cachedValue.RemoveFromLoadedObjects(identityToRemove);
                if (objectToAdd != null) cachedValue.AddToLoadedObjects(objectToAdd);
            }
        }

        internal void UpdateProperty(string propertyName, BrightstarEntityObject newValue)
        {
            _currentPropertyValues[propertyName] = newValue;
        }

        internal void ClearPropertyCache()
        {
            _currentPropertyValues.Clear();
            _currentPropertyCollections.Clear();
        }

        internal void ForceCollectionPropertyUpdates()
        {
            // Force save of complete collection for all literal collection properties
            foreach (var entry in _currentItemValues)
            {
                if (IsLiteralsCollection(entry.Value.GetType()))
                {
                    var propertyUri = GetPropertyUri(entry.Key);
                    var values = DataObject.GetPropertyValues(propertyUri).OfType<object>().ToList();
                    DataObject.RemovePropertiesOfType(propertyUri);
                    foreach(var o in values)
                    {
                        DataObject.AddProperty(propertyUri, o);
                    }
                }
            }
            foreach (var entry in _currentPropertyCollections)
            {
                var properyHint = GetPropertyHint(entry.Key);
                if (properyHint != null && properyHint.MappingType == PropertyMappingType.Arc)
                {
                    var values = DataObject.GetPropertyValues(properyHint.SchemaTypeUri).OfType<DataObject>().ToList();
                    DataObject.RemovePropertiesOfType(properyHint.SchemaTypeUri);
                    foreach (var o in values)
                    {
                        DataObject.AddProperty(properyHint.SchemaTypeUri, o);
                    }
                }
            }
        }

        /// <summary>
        /// Event raised when an entity property is modified
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event
        /// </summary>
        /// <param name="propertyName">The name of the property that has been modified</param>
        protected virtual void OnPropertyChanged(string propertyName)
        {
            var identityInfo = EntityMappingStore.GetIdentityInfo(GetType());
            if (identityInfo != null && identityInfo.KeyProperties != null && identityInfo.KeyProperties.Any(p => p.Name.Equals(propertyName)))
            {
                var newKey = GenerateEntityKey();
                SetKey(newKey);
            }

            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Removes all references to the specified object from locally loaded properties
        /// on this object.
        /// </summary>
        /// <param name="toRemove">The object to be removed</param>
        /// <remarks>This method is used internally when an object is deleted to ensure that the object
        /// is removed from the properties and property collections of all locally tracked object</remarks>
        internal void RemoveReferences(BrightstarEntityObject toRemove)
        {
            var propertyNames = _currentPropertyValues.Where(p => p.Value.Equals(toRemove)).Select(p => p.Key).ToList();
            foreach (var propertyName in propertyNames)
            {
                _currentPropertyValues.Remove(propertyName);
                OnPropertyChanged(propertyName);
            }

           foreach (var c in _currentPropertyCollections.Values)
           {
               c.RemoveFromLoadedObjects(toRemove.Identity);
           }
        }

        internal void TriggerCreatedEvent(BrightstarEntityContext context)
        {
            if (context == null || this.DataObject == null || this.DataObject.IsNew)
            {
                OnCreated(context);
            }
        }

        /// <summary>
        /// Runs after the entity object is created by the specified context.  It is not necessary to call the base method.
        /// </summary>
        /// <param name="context"></param>
        protected virtual void OnCreated(BrightstarEntityContext context)
        {
        }
    }
}
