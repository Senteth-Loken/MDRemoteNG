using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using mRemoteNG.App;
using mRemoteNG.Connection.Protocol.RDP;
using mRemoteNG.Messages;


namespace mRemoteNG.Connection
{
    [SupportedOSPlatform("windows")]
    public class DefaultConnectionInfo : ConnectionInfo
    {
        [Browsable(false)]
        public static DefaultConnectionInfo Instance { get; } = new DefaultConnectionInfo();

        private DefaultConnectionInfo()
        {
            IsDefault = true;
            Inheritance = DefaultConnectionInheritance.Instance;
        }

        public void LoadFrom<TSource>(TSource sourceInstance, Func<string, string> propertyNameMutator = null)
        {
            if (propertyNameMutator == null)
                propertyNameMutator = a => a;

            System.Collections.Generic.IEnumerable<System.Reflection.PropertyInfo> connectionProperties = GetSerializableProperties();
            foreach (System.Reflection.PropertyInfo property in connectionProperties)
            {
                try
                {
                    string expectedPropertyName = propertyNameMutator(property.Name);
                    System.Reflection.PropertyInfo propertyFromSource = typeof(TSource).GetProperty(expectedPropertyName);
                    if (propertyFromSource == null)
                    {
                        // Older settings schemas do not contain every connection property.
                        // Preserve the defaults established by ConnectionInfo in that case.
                        Runtime.MessageCollector?.AddMessage(MessageClass.DebugMsg,
                            $"Default connection setting '{expectedPropertyName}' is unavailable; keeping its current default.");
                        continue;
                    }

                    object valueFromSource = propertyFromSource.GetValue(sourceInstance, null);

                    if (property.PropertyType.IsEnum)
                    {
                        object enumValue = property.PropertyType == typeof(RDPDiskDrives) && valueFromSource is bool redirectDrives
                            ? redirectDrives ? RDPDiskDrives.All : RDPDiskDrives.None
                            : Enum.Parse(property.PropertyType, valueFromSource.ToString());
                        property.SetValue(Instance, enumValue, null);
                        continue;
                    }

                    property.SetValue(Instance, Convert.ChangeType(valueFromSource, property.PropertyType), null);
                }
                catch (Exception ex)
                {
                    Runtime.MessageCollector?.AddExceptionStackTrace($"Error loading default connectioninfo property {property.Name}", ex);
                }
            }
        }

        public void SaveTo<TDestination>(TDestination destinationInstance, Func<string, string> propertyNameMutator = null)
        {
            if (propertyNameMutator == null)
                propertyNameMutator = (a) => a;

            System.Collections.Generic.IEnumerable<System.Reflection.PropertyInfo> connectionProperties = GetSerializableProperties();

            foreach (System.Reflection.PropertyInfo property in connectionProperties)
            {
                try
                {
                    string expectedPropertyName = propertyNameMutator(property.Name);
                    System.Reflection.PropertyInfo propertyFromDestination = typeof(TDestination).GetProperty(expectedPropertyName);

                    if (propertyFromDestination == null)
                    {
                        Runtime.MessageCollector?.AddMessage(MessageClass.DebugMsg,
                            $"Default connection setting '{expectedPropertyName}' is unavailable; skipping it.");
                        continue;
                    }

                    // ensure value is of correct type
                    object currentValue = property.GetValue(Instance, null);
                    object value = currentValue is RDPDiskDrives drives && propertyFromDestination.PropertyType == typeof(bool)
                        ? drives != RDPDiskDrives.None
                        : Convert.ChangeType(currentValue, propertyFromDestination.PropertyType);

                    propertyFromDestination.SetValue(destinationInstance, value, null);
                }
                catch (Exception ex)
                {
                    Runtime.MessageCollector?.AddExceptionStackTrace($"Error saving default connectioninfo property {property.Name}", ex);
                }
            }
        }
    }
}
