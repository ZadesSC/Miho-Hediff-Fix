using System;
using System.Linq;
using System.Collections;
using System.Globalization;
static class MiniJson {
 static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
 public static string Serialize(object o) {
  if(o==null)return "null";
  if(o is string s)return Quote(s);
  if(o is bool b)return b?"true":"false";
  if(o is IDictionary d)return "{"+string.Join(",",d.Keys.Cast<object>().Select(k=>Quote(k.ToString())+":"+Serialize(d[k])))+"}";
  if(o is IEnumerable list)return "["+string.Join(",",list.Cast<object>().Select(Serialize))+"]";
  if(o is IConvertible)return Convert.ToString(o,CultureInfo.InvariantCulture);
  return "{"+string.Join(",",o.GetType().GetProperties().Where(p=>p.GetIndexParameters().Length==0).Select(p=>Quote(p.Name)+":"+Serialize(p.GetValue(o))))+"}";
 }
}
