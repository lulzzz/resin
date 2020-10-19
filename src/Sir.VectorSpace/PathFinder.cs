﻿using System.Collections.Generic;
using System.Text;

namespace Sir.VectorSpace
{
    public static class PathFinder
    {
        public static Hit ClosestMatch(VectorNode root, IVector vector, IModel model)
        {
            var best = root;
            var cursor = root;
            double highscore = 0;
            
            while (cursor != null)
            {
                var angle = cursor.Vector == null ? 0 : model.CosAngle(vector, cursor.Vector);

                if (angle > model.FoldAngle)
                {
                    if (angle > highscore)
                    {
                        highscore = angle;
                        best = cursor;
                    }

                    if (angle >= model.IdenticalAngle)
                    {
                        break;
                    }

                    cursor = cursor.Left;
                }
                else
                {
                    if (angle > highscore)
                    {
                        highscore = angle;
                        best = cursor;
                    }

                    cursor = cursor.Right;
                }
            }

            return new Hit(best, highscore);
        }

        public static IEnumerable<VectorNode> All(VectorNode root)
        {
            var node = root.ComponentCount == 0 ? root.Right : root;
            var stack = new Stack<VectorNode>();

            while (node != null)
            {
                yield return node;

                if (node.Right != null)
                {
                    stack.Push(node.Right);
                }

                node = node.Left;

                if (node == null)
                {
                    if (stack.Count > 0)
                        node = stack.Pop();
                }
            }
        }

        public static string Visualize(VectorNode root)
        {
            StringBuilder output = new StringBuilder();

            foreach(var node in All(root))
            {
                Visualize(node, output);
            }

            return output.ToString();
        }

        private static void Visualize(VectorNode node, StringBuilder output)
        {
            if (node == null) return;

            output.Append('\t',node.Depth);
            output.AppendFormat($"{node} w:{node.Weight}");
            output.AppendLine();
        }

        public static (int depth, int width) Size(VectorNode root)
        {
            var node = root.Right;
            var width = 0;
            var depth = 0;

            while (node != null)
            {
                var d = Depth(node);

                if (d > depth)
                {
                    depth = d;
                }

                width++;

                node = node.Right;
            }

            return (depth, width);
        }

        private static int Depth(VectorNode node)
        {
            var count = 1;

            node = node.Left;

            while (node != null)
            {
                count++;
                node = node.Left;
            }

            return count;
        }
    }
}
