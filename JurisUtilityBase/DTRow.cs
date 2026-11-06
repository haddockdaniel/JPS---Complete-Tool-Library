using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JurisUtilityBase
{
    public class DTRow
    {
        public DTRow() {
            title = "";
            id = 0;
            parent = 0;
            level = 0;
            children = new List<DTRow>();
        }

        public string title { get; set; }
        public int id { get; set; }
        public int parent {  get; set; }
        public int level { get; set; }
        public List<DTRow> children { get; set; }


    }
}
