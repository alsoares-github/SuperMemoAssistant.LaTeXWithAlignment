﻿#region License & Metadata

// The MIT License (MIT)
// 
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
// 
// 
// Created On:   2018/06/02 22:15
// Modified On:  2019/01/14 21:09
// Modified By:  Alexis

#endregion




using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JetBrains.Annotations;
using SuperMemoAssistant.Extensions;

namespace SuperMemoAssistant.Plugins.LaTeX
{
  public class LaTeXDocument
  {
    #region Properties & Fields - Non-Public

    private LaTeXCfg Config    { get; }
    private string   Selection { get; set; }
    private string   Html      { get; set; }

    #endregion




    #region Constructors

    public LaTeXDocument(
      [NotNull] LaTeXCfg config,
      [NotNull] string   html,
      string             selection = null)
    {
      Config    = config;
      Html      = html;
      Selection = selection ?? html;
    }

    #endregion




    #region Methods

    public string ConvertImagesToLaTeX()
    {
      var newSelection  = Selection.Clone() as string;
      var allImagesData = GetAllImagesLaTeXCode();

      foreach (var (html, latex) in allImagesData)
        newSelection = newSelection.ReplaceFirst(html,
                                                 latex.FromBase64());

      Html = Html.Replace(Selection,
                          newSelection);
      Selection = newSelection;

      return Html;
    }

    public string ConvertLaTeXToImages()
    {
      string newSelection = LaTeXConst.RE.LaTeXError.Replace(Selection,
                                                             string.Empty);
      var filters = Config.Filters;

      var allTaggedMatches = filters.Select(
        f => (f.Value, f.Key.Matches(Selection))
      );

      foreach (var taggedMatches in allTaggedMatches)
      {
        var itemsOccurences  = new Dictionary<string, int>();
        var processedMatches = GenerateImages(taggedMatches.Item1,
                                              taggedMatches.Item2);

        foreach (var processedMatch in processedMatches)
        {
          var (success, imgFilePathOrError, latexCode, fullHtml) = processedMatch;

          int nb = itemsOccurences.SafeGet(fullHtml,
                                           0) + 1;
          itemsOccurences[fullHtml] = nb;

          if (success)
            try
            {
              var imgHtml = GenerateImgHtml(imgFilePathOrError,
                                            taggedMatches.Item1.SurroundTexWith(latexCode));
              newSelection = newSelection.ReplaceNth(fullHtml,
                                                     imgHtml,
                                                     nb);
            }
            catch (Exception ex)
            {
              success            = false;
              imgFilePathOrError = ex.Message;
            }

          if (success == false)
            newSelection = newSelection.ReplaceNth(fullHtml,
                                                   GenerateErrorHtml(fullHtml,
                                                                     imgFilePathOrError),
                                                   nb);
        }
      }

      Html = Html.Replace(Selection,
                          newSelection);
      Selection = newSelection;

      return Html;
    }

    private string GenerateImgHtml(string filePath,
                                   string latexCode)
    {
      if (File.Exists(filePath) == false)
        throw new ArgumentException($"File \"{filePath}\" does not exist.");

      string base64Img;

      using (var fileStream = File.OpenRead(filePath))
        base64Img = fileStream.ToBase64();

      var size = GetImageSize(filePath);

      return string.Format(Config.LaTeXImageTag,
                           size.Width,
                           size.Height,
                           base64Img,
                           latexCode.ToBase64());
    }

    private Size GetImageSize(string filePath)
    {
      try
      {
        using (var img = Image.FromFile(filePath))
          return img.Size;
      }
      catch
      {
        // Ignore
      }

      return GetSvgSize(filePath);
    }

    private Size GetSvgSize(string filePath)
    {
      XDocument doc = XDocument.Load(filePath);
      
      if (doc == null)
        throw new ArgumentException($"Output format unsupported \"{filePath}\".");

      var svg = doc.Element("svg");

      if (svg == null)
        throw new ArgumentException($"Output format unsupported \"{filePath}\". Can't find 'svg' element.");

      var widthStr  = svg.Attribute("width")?.Value;
      var heightStr = svg.Attribute("height")?.Value;

      if (widthStr == null || heightStr == null)
        throw new ArgumentException($"Output format unsupported \"{filePath}\". Can't find 'width' and 'height' attributes.");

      var widthRegexRes  = LaTeXConst.RE.SvgPxDimension.Match(widthStr);
      var heightRegexRes = LaTeXConst.RE.SvgPxDimension.Match(heightStr);

      if (widthRegexRes.Success == false || heightRegexRes.Success == false)
        throw new ArgumentException(
          $"Output format unsupported \"{filePath}\". Unknown format for 'width' and 'height' attributes -- should be in '[\\d]+px' format.");

      return new Size(int.Parse(widthRegexRes.Groups[1].Value),
                      int.Parse(heightRegexRes.Groups[1].Value));
    }

    private string GenerateErrorHtml(string html,
                                     string error)
    {
      error = LaTeXUtils.TextToHtml(error ?? string.Empty);

      return html + string.Format(LaTeXConst.Html.LaTeXError,
                                  error);
    }

    private IEnumerable<(bool success, string imgFilePathOrError, string latexCode, string originalHtml)> GenerateImages(
      LaTeXTag        tag,
      MatchCollection matches)
    {
      List<(bool, string, string, string)> ret = new List<(bool, string, string, string)>();

      foreach (Match match in matches)
      {
        string originalHtml = match.Groups[0].Value;
        string latexCode    = match.Groups[1].Value;

        try
        {
          latexCode = LaTeXUtils.PlainText(latexCode);

          var (success, pathOrError) = LaTeXUtils.GenerateDviFile(Config,
                                                                  tag,
                                                                  latexCode);

          if (success == false)
          {
            ret.Add((false, pathOrError, null, originalHtml));
            continue;
          }

          (success, pathOrError) = LaTeXUtils.GenerateImgFile(Config);

          ret.Add((success, pathOrError, latexCode, originalHtml));
        }
        catch (Exception ex)
        {
          ret.Add((false, ex.Message, null, originalHtml));
        }
      }

      return ret;
    }

    private HashSet<(string html, string latex)> GetAllImagesLaTeXCode()
    {
      HashSet<(string, string)> ret     = new HashSet<(string, string)>();
      var                       matches = LaTeXConst.RE.LaTeXImage.Matches(Selection);

      foreach (Match imgMatch in matches)
      {
        var html      = imgMatch.Groups[0].Value;
        var latexCode = LaTeXConst.RE.LaTeXImageLaTeXCode.Match(html);

        if (latexCode.Success)
          ret.Add((html, latexCode.Groups[1].Value));
      }

      return ret;
    }

    #endregion
  }
}
